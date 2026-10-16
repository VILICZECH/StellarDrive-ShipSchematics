using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // The mod's lines on the screen: a status line, the placement hint and a warning. They sit on a canvas of their
    // own above the game's UI and use the game's button font once a game screen has been seen.
    static class ScreenMessages
    {
        const float StatusSeconds = 4f;
        const float WarningSeconds = 12f;
        const float MaxWidth = 900f;

        static Canvas _canvas;
        static Line _status;
        static Line _hint;
        static Line _warning;
        static float _statusUntil;
        static float _warningUntil;
        static bool _gameFont;

        public static void Status(string text)
        {
            if (Show(ref _status, LineKind.Status, text))
                _statusUntil = Time.unscaledTime + StatusSeconds;
        }

        public static void Warning(string text)
        {
            if (Show(ref _warning, LineKind.Warning, text))
                _warningUntil = Time.unscaledTime + WarningSeconds;
        }

        // The placement hint stays until it is replaced or cleared with null.
        public static void Hint(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_hint != null)
                    _hint.Hide();
                return;
            }
            if (_hint == null || !_hint.Showing(text))
                Show(ref _hint, LineKind.Hint, text);
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (_status != null && now > _statusUntil)
                _status.Hide();
            if (_warning != null && now > _warningUntil)
                _warning.Hide();
        }

        static bool Show(ref Line line, LineKind kind, string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            if (_canvas == null)
            {
                CreateCanvas();
                _status = null;
                _hint = null;
                _warning = null;
            }
            if (line == null)
                line = new Line(_canvas.transform, kind);
            if (!_gameFont)
                UseGameFont();
            line.Show(text);
            return true;
        }

        static void CreateCanvas()
        {
            var root = new GameObject("Ship Schematics messages");
            Object.DontDestroyOnLoad(root);
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 500;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _gameFont = false;
        }

        static void UseGameFont()
        {
            GameObject template = ScreenKit.ButtonTemplate();
            TMP_Text sample = template == null ? null : template.GetComponentInChildren<TMP_Text>(true);
            if (sample == null || sample.font == null)
                return;
            _gameFont = true;
            foreach (TMP_Text text in _canvas.GetComponentsInChildren<TMP_Text>(true))
                text.font = sample.font;
            Line.Font = sample.font;
        }

        enum LineKind
        {
            Status,
            Hint,
            Warning
        }

        sealed class Line
        {
            public static TMP_FontAsset Font;

            readonly RectTransform _box;
            readonly RectTransform _textRect;
            readonly TextMeshProUGUI _text;

            public Line(Transform parent, LineKind kind)
            {
                _box = ScreenKit.NewRect(kind + " message", parent);
                Image background = _box.gameObject.AddComponent<Image>();
                background.raycastTarget = false;
                background.color = kind == LineKind.Warning ? new Color(0.32f, 0.12f, 0.04f, 0.9f) : new Color(0.02f, 0.05f, 0.08f, 0.82f);
                Vector2 corner = kind == LineKind.Warning ? new Vector2(0f, 1f) : kind == LineKind.Hint ? new Vector2(0.5f, 0f) : Vector2.zero;
                _box.anchorMin = corner;
                _box.anchorMax = corner;
                _box.pivot = corner;
                _box.anchoredPosition = kind == LineKind.Warning ? new Vector2(24f, -24f) : kind == LineKind.Hint ? new Vector2(0f, 96f) : new Vector2(24f, 24f);

                _textRect = ScreenKit.NewRect("Text", _box);
                _textRect.anchorMin = new Vector2(0.5f, 0.5f);
                _textRect.anchorMax = new Vector2(0.5f, 0.5f);
                _textRect.pivot = new Vector2(0.5f, 0.5f);
                _text = _textRect.gameObject.AddComponent<TextMeshProUGUI>();
                if (Font != null)
                    _text.font = Font;
                else if (TMP_Settings.defaultFontAsset != null)
                    _text.font = TMP_Settings.defaultFontAsset;
                _text.fontSize = 26f;
                _text.color = Color.white;
                _text.alignment = kind == LineKind.Hint ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
                _text.textWrappingMode = TextWrappingModes.Normal;
                _text.raycastTarget = false;
                _box.gameObject.SetActive(false);
            }

            public bool Showing(string text)
            {
                return _box.gameObject.activeSelf && _text.text == text;
            }

            public void Show(string text)
            {
                _text.text = text;
                float width = Mathf.Min(_text.GetPreferredValues(text, MaxWidth, 0f).x, MaxWidth);
                float height = _text.GetPreferredValues(text, width, 0f).y;
                _textRect.sizeDelta = new Vector2(width, height);
                _box.sizeDelta = new Vector2(width + 32f, height + 20f);
                _box.gameObject.SetActive(true);
            }

            public void Hide()
            {
                if (_box != null && _box.gameObject.activeSelf)
                    _box.gameObject.SetActive(false);
            }
        }
    }
}
