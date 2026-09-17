// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.Save();

            DiagLog.Reset();
            Loc.Language = Translator.Setting?.UiLanguage ?? "zh";
            DiagLog.Write($"[App] audioSource={Translator.Setting?.AudioSource} asrProvider={Translator.Setting?.AsrProvider} audioInput={Translator.Setting?.AudioInput}");

            Translator.StartSession();
            // Choose the text source: microphone (iFlytek RTASR) or Windows Live Captions.
            if (Translator.IsMicrophoneMode)
                Translator.StartAudioLoop();
            else
                Task.Run(() => Translator.SyncLoop());

            Task.Run(() => Translator.TranslateLoop());
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Translator.EndSession();

            if (Translator.Window != null)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
            }
        }
    }
}
