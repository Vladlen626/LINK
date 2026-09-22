using System.Globalization;
using System.Speech.Recognition;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class SpeechToTextService : ISpeechToText
{
    public Task<string> TranscribeAsync(string waveFile, VoiceLanguage language, CancellationToken cancellationToken = default) =>
        Task.Run(() => Transcribe(waveFile, language, cancellationToken), cancellationToken);

    private static string Transcribe(string waveFile, VoiceLanguage language, CancellationToken cancellationToken)
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var requestedLanguages = language switch
        {
            VoiceLanguage.Russian => new[] { "ru" },
            VoiceLanguage.English => new[] { "en" },
            _ => new[] { "ru", "en" }
        };

        var candidates = recognizers
            .Where(info => requestedLanguages.Contains(info.Culture.TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            var label = language == VoiceLanguage.Auto ? "Russian or English" : language.ToString();
            throw new InvalidOperationException($"No local Windows speech recognizer is installed for {label}.");
        }

        RecognitionResult? best = null;
        foreach (var recognizerInfo in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
            recognizer.LoadGrammar(new DictationGrammar());
            recognizer.SetInputToWaveFile(waveFile);
            var result = recognizer.Recognize();
            if (result is not null && (best is null || result.Confidence > best.Confidence))
            {
                best = result;
            }
        }

        if (best is null || string.IsNullOrWhiteSpace(best.Text))
        {
            throw new InvalidOperationException("Speech was not recognized. Try holding push-to-talk closer to the microphone.");
        }

        return best.Text.Trim();
    }
}
