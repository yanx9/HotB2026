using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Bci.Signals;

namespace Precel.Game
{
    // Napisy na ekranie. Panel w prawym dolnym rogu pokazuje wszystkie sygnały, jakie
    // dotarły do gry, i to, które z nich sterują nożem.
    public class Hud
    {
        public Text title, status, message, controls, signals;
        readonly StringBuilder builder = new StringBuilder();

        public void Build(Transform root)
        {
            var canvasGo = new GameObject("Napisy");
            canvasGo.transform.SetParent(root, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 1f;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            title = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 30, new Vector2(0f, -14f));
            status = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 20, new Vector2(0f, -56f));
            message = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 28, new Vector2(0f, -100f));
            controls = MakeText(canvasGo.transform, font, TextAnchor.LowerLeft, 15, new Vector2(16f, 12f));
            signals = MakeText(canvasGo.transform, font, TextAnchor.LowerRight, 15, new Vector2(-16f, 12f));

            title.text = "MISTRZOSTWA W KROJENIU PRECLA NA PÓŁ";
            title.color = new Color(1f, 0.95f, 0.8f);
            controls.text = "← / →  nóż w lewo i w prawo\nSpacja (trzymaj)  cięcie\nklawisze udają sygnały z EEG";
        }

        static Text MakeText(Transform parent, Font font, TextAnchor anchor, int size, Vector2 offset)
        {
            var go = new GameObject("Tekst");
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(Mathf.Max(0f, offset.x), Mathf.Max(0f, offset.y));
            rect.offsetMax = new Vector2(Mathf.Min(0f, offset.x), Mathf.Min(0f, offset.y));
            return text;
        }

        public void ShowSignals(SignalBindings bindings)
        {
            builder.Clear();
            builder.AppendLine("SYGNAŁY");
            if (SignalHub.All.Count == 0) builder.AppendLine("(żaden jeszcze nie dotarł)");
            foreach (var signal in SignalHub.All)
            {
                bool fresh = SignalHub.TryGet(signal.name, out float value);
                builder.Append(signal.name).Append("  ").Append(fresh ? value.ToString("+0.00;-0.00") : "–")
                    .Append("  ").AppendLine(signal.source);
            }
            builder.AppendLine();
            foreach (var binding in bindings.config.bindings)
            {
                builder.Append(binding.action).Append(" ← ").Append(binding.source ?? binding.signal).Append("  ");
                builder.AppendLine(binding.live ? binding.value.ToString("+0.00;-0.00") : "brak sygnału");
            }
            bool bridge = Time.unscaledTime - UdpSignalSource.LastPacketAt < 2f;
            builder.Append(bridge ? "potok BCI: " + UdpSignalSource.LastSender : "potok BCI: brak danych");
            signals.text = builder.ToString();
        }
    }
}
