using System.Collections.Generic;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingPrepProgress(int Completed, int Total, string Message);

public sealed record SpeakingPrepOutcome(SpeakingSession? Session, IReadOnlyList<string> Errors);

/// <summary>
/// Prepares a whole speaking paper for automation: resolves each answer,
/// synthesizes its TTS WAV, and saves the session used by the player.
/// </summary>
public sealed class SpeakingPrepService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly SpeakingAnswerService _answers;
    private readonly DeepSeekService _deepSeek;
    private readonly AliyunTtsService _tts;
    private readonly SpeakingSessionStore _sessionStore;

    public SpeakingPrepService(string appRoot, string dataRoot)
    {
        _answers = new SpeakingAnswerService(dataRoot);
        _deepSeek = new DeepSeekService(appRoot);
        _tts = new AliyunTtsService(appRoot);
        _sessionStore = new SpeakingSessionStore(appRoot);
    }

    public IReadOnlyList<SpeakingQuestion> LoadQuestions(string packId) => _answers.Load(packId);

    /// <summary>
    /// Returns the audio prepared for this paper on an earlier visit when it is
    /// still usable, so leaving and re-entering a simulated exam does not pay
    /// for the whole paper again.
    /// </summary>
    private SpeakingSession? FindReusableSession(string packId, string mode,
        IReadOnlyList<SpeakingQuestion> questions)
    {
        SpeakingSession? session = _sessionStore.Load();
        if (session is null || session.PackId != packId) return null;
        if (!string.Equals(session.Mode, mode, StringComparison.Ordinal)) return null;
        if (session.Items.Count != questions.Count) return null;
        if (!session.Items.Select(item => item.Number).SequenceEqual(questions.Select(question => question.Number)))
            return null;
        return session.Items.All(item => SpeakingSessionStore.IsUsableWavFile(item.WavPath))
            ? session
            : null;
    }

    public async Task<SpeakingPrepOutcome> PrepareAsync(string packId,
        IProgress<SpeakingPrepProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SpeakingQuestion> questions = _answers.Load(packId);
        if (questions.Count == 0) return new SpeakingPrepOutcome(null, Array.Empty<string>());

        string mode = AppPreferencesService.Current.SpeakingMode;
        if (mode == "flexible" && !ApiKeyStore.HasKey)
            return new SpeakingPrepOutcome(null, new[] { "未填写有效的凭据\n无法完成指定操作。" });

        SpeakingSession? reusable = FindReusableSession(packId, mode, questions);
        if (reusable is not null)
        {
            progress?.Report(new SpeakingPrepProgress(reusable.Items.Count, questions.Count,
                $"已有 {reusable.Items.Count} 条可用音频，无需重新生成"));
            return new SpeakingPrepOutcome(reusable, Array.Empty<string>());
        }

        if (!AliyunTtsCredentialStore.HasCredentials)
        {
            return new SpeakingPrepOutcome(null, new[]
            {
                "未填写有效的凭据\n无法完成指定操作。"
            });
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<SpeakingSessionItem>();
            var errors = new List<string>();
            int completed = 0;
            progress?.Report(new SpeakingPrepProgress(0, questions.Count,
                $"准备 {questions.Count} 道口语题 · {(mode == "flexible" ? "生成模式" : "标答模式")}"));

            foreach (SpeakingQuestion question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SpeakingPrepProgress(completed, questions.Count,
                    $"正在处理 {question.Number}/{questions.Count}：{question.Type}"));

                string answer = question.StandardAnswer;
                if (mode == "flexible")
                {
                    DeepSeekResult generated = await _deepSeek
                        .AskSpeakingAsync(question.RequestText, cancellationToken).ConfigureAwait(false);
                    if (generated.Success && generated.Text.Trim().Length > 0)
                        answer = generated.Text.Trim();
                    else
                        errors.Add($"第 {question.Number} 题：DeepSeek 生成失败，已改用标答（{generated.Error}）");
                }
                if (string.IsNullOrWhiteSpace(answer))
                {
                    errors.Add($"第 {question.Number} 题：没有可用答案");
                    completed++;
                    continue;
                }

                AliyunTtsResult tts = await _tts.SynthesizeToWavAsync(answer, cancellationToken)
                    .ConfigureAwait(false);
                if (!tts.Success)
                {
                    errors.Add($"第 {question.Number} 题：TTS 失败（{tts.Error}）");
                    completed++;
                    continue;
                }

                items.Add(new SpeakingSessionItem(question.Number, question.Type, question.Prompt, answer,
                    tts.FilePath));
                completed++;
                progress?.Report(new SpeakingPrepProgress(completed, questions.Count,
                    $"已完成 {completed}/{questions.Count}：{question.Type}"));
            }

            if (items.Count == 0)
                return new SpeakingPrepOutcome(null, errors.Count > 0 ? errors : new[] { "没有生成任何音频。" });

            var session = new SpeakingSession(packId, mode, DateTimeOffset.Now, items);
            _sessionStore.Save(session);
            progress?.Report(new SpeakingPrepProgress(items.Count, questions.Count,
                $"完成：{items.Count}/{questions.Count} 条音频"));
            return new SpeakingPrepOutcome(session, errors);
        }
        finally
        {
            Gate.Release();
        }
    }
}
