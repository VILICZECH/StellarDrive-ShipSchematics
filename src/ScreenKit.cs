using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UI.BuildMenu;
using UI.Common;
using UI.Inventory;
using UI.MainMenu;
using UI.Research;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // Builds the schematic windows from the game's own screens: the build menu frame, the Research button, the world
    // name field and rename dialog of the main menu, and the inventory title.
    static class ScreenKit
    {
        static readonly AccessTools.FieldRef<ButtonBase, TMP_Text> CardText =
            AccessTools.FieldRefAccess<ButtonBase, TMP_Text>("text");
        static readonly AccessTools.FieldRef<ButtonBase, bool> CardSelected =
            AccessTools.FieldRefAccess<ButtonBase, bool>("_isSelected");

        static Transform _templates;
        static GameObject _buttonTemplate;
        static TMP_InputField _nameTemplate;
        static GameObject _renameTemplate;
        static Sprite _importIcon;
        static Sprite _exportIcon;
        static float _titleSize = 48f;
        static TMP_FontAsset _titleFont;
        static bool _titleRead;

        // Clones are made under this inactive root, so they can be set up before their Awake runs.
        static Transform Templates
        {
            get
            {
                if (_templates == null)
                {
                    var root = new GameObject("Schematic UI templates");
                    root.SetActive(false);
                    Object.DontDestroyOnLoad(root);
                    _templates = root.transform;
                }
                return _templates;
            }
        }

        public static T Read<T>(object owner, string name)
        {
            if (owner == null)
                return default;
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return default;
            object value = field.GetValue(owner);
            if (value is T typed)
                return typed;
            return default;
        }

        static GameObject Clone(GameObject template, Transform parent, string name, Action<GameObject> setup)
        {
            GameObject clone = Object.Instantiate(template, Templates);
            clone.name = name;
            if (setup != null)
                setup(clone);
            clone.transform.SetParent(parent, false);
            clone.SetActive(true);
            return clone;
        }

        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        public static void Clear(Transform root)
        {
            if (root == null)
                return;
            for (int i = root.childCount - 1; i >= 0; i--)
                Object.Destroy(root.GetChild(i).gameObject);
        }

        public static void FreezeLayout(Transform panel)
        {
            if (panel == null)
                return;
            foreach (Behaviour behaviour in panel.GetComponents<Behaviour>())
            {
                if (behaviour is LayoutGroup || behaviour is ContentSizeFitter)
                    behaviour.enabled = false;
            }
        }

        // A nested canvas above the build menu's own graphics, with its own raycaster for the mod's buttons.
        public static RectTransform MakeContent(Transform panel)
        {
            RectTransform root = NewRect("SchematicContent", panel);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;
            root.SetAsLastSibling();
            Canvas canvas = root.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 30;
            root.gameObject.AddComponent<GraphicRaycaster>();
            return root;
        }

        public static void SilenceLocalization(GameObject go)
        {
            foreach (LocalizeStringEvent localize in go.GetComponentsInChildren<LocalizeStringEvent>(true))
                localize.enabled = false;
        }

        public static void HideChrome(BuildMenu menu)
        {
            Transform tabs = Read<Transform>(menu, "tabButtonsContainer");
            Component partInfo = Read<Component>(menu, "partInfo");
            GameObject keyTip = Read<GameObject>(menu, "keyTip");
            if (tabs != null)
                tabs.gameObject.SetActive(false);
            if (partInfo != null)
                partInfo.gameObject.SetActive(false);
            if (keyTip != null)
                keyTip.SetActive(false);
        }

        public static Vector2 PanelSize(Transform panel)
        {
            RectTransform rect = panel as RectTransform;
            float width = rect != null ? rect.rect.width : 0f;
            float height = rect != null ? rect.rect.height : 0f;
            if (width < 200f)
                width = Screen.width * 0.62f;
            if (height < 200f)
                height = Screen.height * 0.62f;
            return new Vector2(width, height);
        }

        public static TMP_FontAsset FontFrom(BuildPartButton prefab)
        {
            ButtonBase card = prefab == null ? null : prefab.GetComponent<ButtonBase>();
            TMP_Text text = card == null ? null : CardText(card);
            return text == null ? null : text.font;
        }

        // The action button of the Research menu, the same style as the game's dialog buttons.
        public static GameObject ButtonTemplate()
        {
            if (_buttonTemplate != null)
                return _buttonTemplate;
            GenericPartOpenableMenuActivator activator = Object.FindAnyObjectByType<GenericPartOpenableMenuActivator>(FindObjectsInactive.Include);
            GameObject prefab = activator == null ? null : Read<GameObject>(activator, "researchMenuPrefab");
            if (prefab == null)
                return null;

            GameObject menu = Object.Instantiate(prefab, Templates);
            Button source = null;
            ResearchInfo info = menu.GetComponentInChildren<ResearchInfo>(true);
            if (info != null)
                source = Read<Button>(info, "researchButton");
            if (source == null)
            {
                foreach (Transform child in menu.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name == "ResearchButton")
                    {
                        source = child.GetComponent<Button>();
                        if (source != null)
                            break;
                    }
                }
            }
            if (source != null)
            {
                _buttonTemplate = Object.Instantiate(source.gameObject, Templates);
                _buttonTemplate.name = "Schematic action button";
            }
            Object.Destroy(menu);
            return _buttonTemplate;
        }

        public static Vector2 ButtonSize(string label)
        {
            float height = 52f;
            float width = Mathf.Max(168f, PreferredLabelWidth(label) + 64f);
            if (_buttonTemplate != null)
            {
                RectTransform source = _buttonTemplate.GetComponent<RectTransform>();
                if (source != null)
                {
                    float sourceHeight = source.rect.height > 12f ? source.rect.height : source.sizeDelta.y;
                    if (sourceHeight > 12f)
                        height = sourceHeight;
                }
            }
            return new Vector2(width, height);
        }

        static float PreferredLabelWidth(string label)
        {
            TMP_Text sample = _buttonTemplate == null ? null : _buttonTemplate.GetComponentInChildren<TMP_Text>(true);
            if (sample != null)
            {
                bool auto = sample.enableAutoSizing;
                sample.enableAutoSizing = false;
                float width = sample.GetPreferredValues(label).x;
                sample.enableAutoSizing = auto;
                if (width > 8f)
                    return width;
            }
            return label.Length * 22f;
        }

        public static RectTransform SpawnButton(Transform parent, string label, Action onClick)
        {
            GameObject template = ButtonTemplate();
            GameObject clone;
            if (template != null)
            {
                clone = Clone(template, parent, label, button =>
                {
                    SilenceLocalization(button);
                    foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
                    {
                        text.enableAutoSizing = false;
                        text.text = label;
                        text.alignment = TextAlignmentOptions.Center;
                        text.overflowMode = TextOverflowModes.Overflow;
                        text.textWrappingMode = TextWrappingModes.NoWrap;
                        RectTransform textRect = text.rectTransform;
                        textRect.anchorMin = Vector2.zero;
                        textRect.anchorMax = Vector2.one;
                        textRect.offsetMin = new Vector2(18f, 4f);
                        textRect.offsetMax = new Vector2(-18f, -4f);
                    }
                });
            }
            else
            {
                clone = NewRect(label, parent).gameObject;
                Image image = clone.AddComponent<Image>();
                image.color = new Color(0.16f, 0.45f, 0.78f, 1f);
                RectTransform textRect = NewRect("Label", clone.transform);
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                TextMeshProUGUI text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
                text.text = label;
                text.fontSize = 18f;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Color.white;
                if (TMP_Settings.defaultFontAsset != null)
                    text.font = TMP_Settings.defaultFontAsset;
            }

            LayoutElement layout = clone.GetComponent<LayoutElement>();
            if (layout == null)
                layout = clone.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;

            Button button = clone.GetComponent<Button>();
            if (button == null)
                button = clone.AddComponent<Button>();
            button.interactable = true;
            button.onClick = new Button.ButtonClickedEvent();
            if (onClick != null)
                button.onClick.AddListener(() => onClick());
            foreach (Graphic graphic in clone.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = true;
            CanvasGroup group = clone.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
            return clone.GetComponent<RectTransform>();
        }

        public static RectTransform PlaceSized(Transform parent, float x, float y, float width, float height, string label, Action onClick)
        {
            RectTransform rect = SpawnButton(parent, label, onClick);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        public static RectTransform PlaceButton(Transform parent, float x, float y, string label, Action onClick)
        {
            Vector2 size = ButtonSize(label);
            return PlaceSized(parent, x, y, size.x, size.y, label, onClick);
        }

        // Lays the buttons out leftwards from areaWidth, the last label rightmost.
        public static void PlaceRight(Transform parent, float areaWidth, float y, string[] labels, Action[] actions)
        {
            float x = areaWidth - 20f;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                x -= ButtonSize(labels[i]).x;
                PlaceButton(parent, x, y, labels[i], actions[i]);
                x -= 16f;
            }
        }

        public static void SetLabel(RectTransform button, string label)
        {
            if (button == null)
                return;
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
                text.text = label;
        }

        public static void PaintButton(Transform root, string name, Color color)
        {
            foreach (Button button in root.GetComponentsInChildren<Button>(true))
            {
                if (button.gameObject.name != name)
                    continue;
                ActionButtonStylization style = button.GetComponent<ActionButtonStylization>();
                if (style != null)
                {
                    if (style.background != null)
                        style.background.color = color;
                    if (style.border != null)
                        style.border.color = color;
                    continue;
                }
                foreach (Image image in button.GetComponentsInChildren<Image>(true))
                    image.color = color;
            }
        }

        public static void AddVerticalRule(Transform parent, float x, float y, float height)
        {
            RectTransform rect = NewRect("Rule", parent);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(3f, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.55f, 0.82f, 1f, 0.95f);
            image.raycastTarget = false;
        }

        public static RectTransform MakeIconButton(Transform parent, string name, bool export, Action onClick)
        {
            RectTransform rect = SpawnButton(parent, name, onClick);
            foreach (TMP_Text text in rect.GetComponentsInChildren<TMP_Text>(true))
            {
                text.text = "";
                text.gameObject.SetActive(false);
            }
            RectTransform glyph = NewRect("Icon", rect);
            glyph.anchorMin = new Vector2(0.22f, 0.22f);
            glyph.anchorMax = new Vector2(0.78f, 0.78f);
            glyph.offsetMin = Vector2.zero;
            glyph.offsetMax = Vector2.zero;
            Image image = glyph.gameObject.AddComponent<Image>();
            image.sprite = export ? ExportIcon : ImportIcon;
            image.color = Color.white;
            image.raycastTarget = false;
            image.preserveAspect = true;
            return rect;
        }

        static Sprite ImportIcon
        {
            get
            {
                if (_importIcon == null)
                    _importIcon = ArrowIcon(false);
                return _importIcon;
            }
        }

        static Sprite ExportIcon
        {
            get
            {
                if (_exportIcon == null)
                    _exportIcon = ArrowIcon(true);
                return _exportIcon;
            }
        }

        static Sprite ArrowIcon(bool pointingUp)
        {
            const int size = 32;
            var pixels = new Color32[size * size];
            Box(pixels, size, 5, 4, 26, 7);
            Box(pixels, size, 5, 4, 8, 12);
            Box(pixels, size, 23, 4, 26, 12);
            Box(pixels, size, 14, pointingUp ? 12 : 15, 17, pointingUp ? 22 : 26);
            for (int row = 0; row < 6; row++)
            {
                int y = pointingUp ? 28 - row : 9 + row;
                int inset = 5 - row;
                Box(pixels, size, 10 + inset, y, 21 - inset, y);
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        static void Box(Color32[] pixels, int size, int x0, int y0, int x1, int y1)
        {
            var ink = new Color32(255, 255, 255, 255);
            for (int y = Math.Max(0, y0); y <= Math.Min(size - 1, y1); y++)
            {
                for (int x = Math.Max(0, x0); x <= Math.Min(size - 1, x1); x++)
                    pixels[y * size + x] = ink;
            }
        }

        // The world name field of the main menu. It only exists there, so it is copied when a scene loads.
        public static void RememberNameField()
        {
            RememberRenameDialog();
            if (_nameTemplate != null)
                return;
            TMP_InputField source = null;
            CreateWorldModal create = Object.FindAnyObjectByType<CreateWorldModal>(FindObjectsInactive.Include);
            if (create != null)
                source = create.nameField;
            if (source == null)
            {
                RenameWorldModal rename = Object.FindAnyObjectByType<RenameWorldModal>(FindObjectsInactive.Include);
                if (rename != null)
                    source = rename.nameField;
            }
            if (source == null)
                return;
            GameObject kept = Object.Instantiate(source.gameObject, Templates);
            kept.name = "Schematic name field";
            _nameTemplate = kept.GetComponent<TMP_InputField>();
        }

        public static TMP_InputField AddNameField(Transform parent, string value, TMP_FontAsset font, int characterLimit)
        {
            RememberNameField();
            TMP_InputField input;
            if (_nameTemplate != null)
            {
                GameObject clone = Clone(_nameTemplate.gameObject, parent, "Name", SilenceLocalization);
                input = clone.GetComponent<TMP_InputField>();
                input.onValueChanged = new TMP_InputField.OnChangeEvent();
                input.onSubmit = new TMP_InputField.SubmitEvent();
                input.onEndEdit = new TMP_InputField.SubmitEvent();
                if (input.placeholder is TMP_Text placeholder)
                    placeholder.text = "";
            }
            else
            {
                RectTransform rect = NewRect("Name", parent);
                rect.sizeDelta = new Vector2(640f, 72f);
                Image image = rect.gameObject.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0.55f);
                input = rect.gameObject.AddComponent<TMP_InputField>();
                RectTransform textRect = NewRect("Text", rect);
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(16f, 8f);
                textRect.offsetMax = new Vector2(-16f, -8f);
                var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
                text.font = font;
                text.fontSize = 36f;
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.MidlineLeft;
                input.textViewport = textRect;
                input.textComponent = text;
            }
            input.characterLimit = characterLimit;
            input.text = value ?? "";
            return input;
        }

        static void RememberRenameDialog()
        {
            if (_renameTemplate != null)
                return;
            GameObject source = null;
            RenameWorldModal rename = Object.FindAnyObjectByType<RenameWorldModal>(FindObjectsInactive.Include);
            if (rename != null)
                source = rename.gameObject;
            if (source == null)
            {
                CreateWorldModal create = Object.FindAnyObjectByType<CreateWorldModal>(FindObjectsInactive.Include);
                if (create != null)
                    source = create.gameObject;
            }
            if (source == null)
                return;
            _renameTemplate = Object.Instantiate(source, Templates);
            _renameTemplate.name = "Schematic rename dialog";
            _renameTemplate.SetActive(false);
            DisableWorldModal(_renameTemplate);
        }

        public static GameObject RenameTemplate
        {
            get
            {
                RememberRenameDialog();
                return _renameTemplate;
            }
        }

        // The modal's own script would rename a world on Enter and keep its button off for names that are not valid
        // world names.
        public static void DisableWorldModal(GameObject dialog)
        {
            foreach (RenameWorldModal modal in dialog.GetComponents<RenameWorldModal>())
                modal.enabled = false;
            foreach (CreateWorldModal modal in dialog.GetComponents<CreateWorldModal>())
                modal.enabled = false;
        }

        public static TMP_InputField NameFieldOf(GameObject dialog)
        {
            RenameWorldModal rename = dialog.GetComponent<RenameWorldModal>();
            if (rename != null && rename.nameField != null)
                return rename.nameField;
            CreateWorldModal create = dialog.GetComponent<CreateWorldModal>();
            if (create != null && create.nameField != null)
                return create.nameField;
            return dialog.GetComponentInChildren<TMP_InputField>(true);
        }

        public static Button ConfirmButton(GameObject dialog)
        {
            RenameWorldModal rename = dialog.GetComponent<RenameWorldModal>();
            if (rename != null && rename.createButton != null)
                return rename.createButton;
            CreateWorldModal create = dialog.GetComponent<CreateWorldModal>();
            if (create != null && create.createButton != null)
                return create.createButton;
            return null;
        }

        public static Button CancelButton(GameObject dialog, Button confirm)
        {
            Button fallback = null;
            foreach (Button button in dialog.GetComponentsInChildren<Button>(true))
            {
                if (button == confirm)
                    continue;
                if (fallback == null)
                    fallback = button;
                for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                {
                    if (button.onClick.GetPersistentMethodName(i) == "Cancel")
                        return button;
                }
            }
            return fallback;
        }

        public static void Quiet(Button button)
        {
            if (button == null)
                return;
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                button.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            button.onClick.RemoveAllListeners();
            button.interactable = true;
        }

        public static void SetButtonLabel(Button button, string label)
        {
            if (button == null)
                return;
            SilenceLocalization(button.gameObject);
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
                text.text = label;
        }

        public static void SetDialogTitle(GameObject dialog, TMP_InputField field, string label)
        {
            TMP_Text chosen = null;
            foreach (TMP_Text text in dialog.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.GetComponentInParent<Button>() != null)
                    continue;
                if (field != null && text.transform.IsChildOf(field.transform))
                    continue;
                string name = text.gameObject.name;
                if (name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chosen = text;
                    break;
                }
                if (chosen == null)
                    chosen = text;
            }
            if (chosen == null)
                return;
            SilenceLocalization(chosen.gameObject);
            chosen.text = label;
        }

        public static void CenterRect(RectTransform rect)
        {
            if (rect == null)
                return;
            Vector2 size = rect.rect.size;
            if (size.x < 40f || size.y < 40f)
                size = rect.sizeDelta;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            if (size.x > 40f && size.y > 40f)
                rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
        }

        // The window title in the size and font of the inventory title.
        public static RectTransform AddTitle(Transform parent, string label, TMP_FontAsset font)
        {
            RectTransform rect = NewRect("Title", parent);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            ReadInventoryTitle();
            rect.sizeDelta = new Vector2(900f, _titleSize + 16f);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = _titleSize;
            text.fontStyle = FontStyles.Normal;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;
            text.raycastTarget = false;
            if (_titleFont != null)
                text.font = _titleFont;
            else if (font != null)
                text.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                text.font = TMP_Settings.defaultFontAsset;
            return rect;
        }

        static void ReadInventoryTitle()
        {
            if (_titleRead)
                return;
            _titleRead = true;
            PlayerInventoryActivator activator = Object.FindAnyObjectByType<PlayerInventoryActivator>(FindObjectsInactive.Include);
            GameObject prefab = activator == null ? null : Read<GameObject>(activator, "inventoryCanvasPrefab");
            if (prefab == null)
                return;
            GameObject sample = Object.Instantiate(prefab, Templates);
            float titledSize = 0f;
            TMP_FontAsset titledFont = null;
            foreach (TMP_Text text in sample.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.GetComponentInParent<Button>() != null)
                    continue;
                string blob = (text.gameObject.name + " " + text.text).ToLowerInvariant();
                bool titled = blob.Contains("inventory") || blob.Contains("title") || blob.Contains("header");
                if (titled && text.fontSize > titledSize)
                {
                    titledSize = text.fontSize;
                    titledFont = text.font;
                }
                if (text.fontSize > _titleSize)
                {
                    _titleSize = text.fontSize;
                    _titleFont = text.font;
                }
            }
            if (titledSize > 8f)
            {
                _titleSize = titledSize;
                _titleFont = titledFont;
            }
            Object.Destroy(sample);
        }

        // The build menu frame: the largest rect between the panel and its canvas that is not the whole screen.
        public static RectTransform WindowRoot(Transform panel, RectTransform canvasRect)
        {
            float limitW = Mathf.Abs(canvasRect.rect.width) * 0.9f;
            float limitH = Mathf.Abs(canvasRect.rect.height) * 0.9f;
            RectTransform window = panel as RectTransform;
            Transform cursor = panel;
            while (cursor != null)
            {
                if (cursor is RectTransform rect)
                {
                    float width = Mathf.Abs(rect.rect.width);
                    float height = Mathf.Abs(rect.rect.height);
                    if (width >= limitW && height >= limitH)
                        break;
                    if (width > 200f && height > 160f)
                        window = rect;
                }
                if (cursor.GetComponent<Canvas>() != null)
                    break;
                cursor = cursor.parent;
            }
            return window;
        }

        public static void NudgeToCenter(RectTransform piece, RectTransform canvasRect)
        {
            var corners = new Vector3[4];
            piece.GetWorldCorners(corners);
            var canvasCorners = new Vector3[4];
            canvasRect.GetWorldCorners(canvasCorners);
            Vector3 delta = (canvasCorners[0] + canvasCorners[2]) * 0.5f - (corners[0] + corners[2]) * 0.5f;
            delta.z = 0f;
            if (delta.sqrMagnitude >= 1f)
                piece.position += delta;
        }

        // The thin line under the window title: a wide, flat graphic in the top part of the frame.
        public static RectTransform FindSeparator(RectTransform window)
        {
            if (window == null)
                return null;
            var frame = new Vector3[4];
            window.GetWorldCorners(frame);
            float frameTop = frame[1].y;
            float frameHeight = frameTop - frame[0].y;
            var corners = new Vector3[4];
            RectTransform best = null;
            float bestScore = 0f;
            foreach (Graphic graphic in window.GetComponentsInChildren<Graphic>(true))
            {
                if (!graphic.gameObject.activeInHierarchy)
                    continue;
                RectTransform rect = graphic.rectTransform;
                if (rect == window)
                    continue;
                float width = Mathf.Abs(rect.rect.width);
                float height = Mathf.Abs(rect.rect.height);
                if (width < Mathf.Abs(window.rect.width) * 0.45f)
                    continue;
                if (height < 0.2f || height > 24f || width < height * 8f)
                    continue;
                rect.GetWorldCorners(corners);
                float fromTop = frameTop - corners[1].y;
                if (fromTop < 0f || fromTop > frameHeight * 0.28f)
                    continue;
                float score = width / Mathf.Max(height, 0.1f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rect;
                }
            }
            return best;
        }

        // A ship card is the build menu's part button without its part: it keeps the look and the hover colours.
        public static ButtonBase MakeCard(Transform parent, BuildPartButton prefab, string label, Action onClick, out TMP_Text text)
        {
            BuildPartButton part = Object.Instantiate(prefab, parent);
            part.enabled = false;
            Button button = part.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                if (onClick != null)
                    button.onClick.AddListener(() => onClick());
            }
            ButtonBase card = part.GetComponent<ButtonBase>();
            text = CardText(card);
            if (text != null)
            {
                text.text = label;
                text.overflowMode = TextOverflowModes.Ellipsis;
            }
            return card;
        }

        public static void SetCardSelected(ButtonBase card, bool selected)
        {
            if (card == null)
                return;
            if (selected)
            {
                card.MarkSelected();
                return;
            }
            CardSelected(card) = false;
            card.MarkAvailable();
        }
    }
}
