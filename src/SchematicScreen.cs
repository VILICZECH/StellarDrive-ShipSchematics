using System;
using System.Collections.Generic;
using Core.Values;
using MelonLoader;
using Ships.Interface.Model.State;
using TMPro;
using UI.BuildMenu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // The ship list and the save window. Both are the game's build menu with its tabs and parts replaced.
    public class SchematicScreen : MonoBehaviour
    {
        enum SortOrder
        {
            Name,
            Newest,
            Size
        }

        const int NameLimit = 64;
        const int SavePictureSize = 512;
        const float Pad = 20f;
        const float Gap = 12f;
        const float CardInset = 6f;
        const float DoubleClick = 0.35f;
        static readonly string[] SortCaptions = { "Sort: Name", "Sort: Newest", "Sort: Size" };
        static readonly Vector3[] Frame = new Vector3[4];
        static readonly Vector3[] Line = new Vector3[4];

        static int _openCount;
        static SchematicScreen _active;

        BuildMenuActivator _activator;
        Action _onClosed;
        bool _closed;
        bool _holdingOpen;
        bool _centered;
        Transform _panel;
        RectTransform _window;
        RectTransform _canvasRect;
        RectTransform _title;
        RectTransform _separator;
        int _separatorTries;
        RectTransform _importButton;
        RectTransform _exportButton;
        Vector4 _snapped = new Vector4(float.NaN, 0f, 0f, 0f);
        float _snappedScale;
        readonly List<GameObject> _hiddenDots = new List<GameObject>();

        Action<SchematicFile> _onPlace;
        Action<SchematicFile> _onDelete;
        Action<string> _onNotice;
        readonly Dictionary<string, ShipCard> _cards = new Dictionary<string, ShipCard>();
        readonly List<ShipCard> _shown = new List<ShipCard>();
        ShipCard _selected;
        ShipCard _lastClicked;
        float _lastClickTime = -1f;
        ThumbnailBaker _baker;
        BuildPartButton _cardPrefab;
        RectTransform _viewport;
        RectTransform _cardRoot;
        TMP_InputField _filterInput;
        RectTransform _sortButton;
        string _query = "";
        SortOrder _sort;
        float _cardWidth = 110f;
        float _cardHeight = 110f;
        float _fittedWidth = 110f;
        float _fittedHeight = 110f;
        float _cardScale = 1f;
        int _columns = 1;
        GameObject _renameDialog;
        GameObject _renameOverlay;
        TMP_InputField _renameField;

        SchematicPreview _preview;
        RectTransform _picture;
        bool _rotating;
        TMP_InputField _nameInput;

        public static bool IsOpen => _openCount > 0;

        public static SchematicScreen OpenLibrary(Action onClosed, Action<SchematicFile> onPlace, Action<SchematicFile> onDelete, Action<string> onNotice)
        {
            SchematicScreen screen = OpenShell(onClosed);
            if (screen == null)
                return null;
            try
            {
                screen._onNotice = onNotice;
            screen.FillLibrary(onPlace, onDelete);
            return screen;
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
                screen.Abandon();
                return null;
            }
        }

        // onSave gets the name, the PNG picture and whether to save as a new ship instead of overwriting.
        public static SchematicScreen OpenSave(IShipState ship, SchematicFile file, string suggestedName, bool alreadySaved, Action onClosed, Action<string, byte[], bool> onSave)
        {
            SchematicScreen screen = OpenShell(onClosed);
            if (screen == null)
                return null;
            try
            {
                screen.FillSave(ship, file, suggestedName, alreadySaved, onSave);
            return screen;
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
                screen.Abandon();
                return null;
            }
        }

        // Escape closes the rename dialog first, not the whole window.
        public static bool CloseDialog()
        {
            if (_active == null || _active._renameDialog == null)
                return false;
            _active.CloseRename();
            return true;
        }

        public bool IsTyping
        {
            get
            {
                if (_renameDialog != null)
                    return true;
                if (Focused(_filterInput) || Focused(_nameInput) || Focused(_renameField))
                    return true;
                EventSystem events = EventSystem.current;
                GameObject selected = events == null ? null : events.currentSelectedGameObject;
                return selected != null && selected.GetComponentInParent<TMP_InputField>() != null;
            }
        }

        static bool Focused(TMP_InputField field)
        {
            return field != null && field.isFocused;
        }

        public void Back()
        {
            if (_renameDialog != null)
                CloseRename();
            else
                Close();
        }

        public void Close()
        {
            Shutdown(true);
        }

        void OnDestroy()
        {
            Shutdown(false);
        }

        void Abandon()
        {
            _onClosed = null;
            Close();
        }

        void Shutdown(bool closeMenu)
        {
            if (_closed)
                return;
            _closed = true;
            if (_holdingOpen)
            {
                _holdingOpen = false;
                if (_openCount > 0)
                    _openCount--;
            }
            if (_active == this)
                _active = null;
            if (_preview != null)
                _preview.Hide();
            if (_baker != null)
                _baker.Clear();
            foreach (ShipCard card in _cards.Values)
                card.ReleasePicture();
            _cards.Clear();
            _shown.Clear();
            _selected = null;
            CloseRename();
            ShowPointerDot();
            GameplayLock.SetWindow(IsOpen);
            Action closed = _onClosed;
            _onClosed = null;
            if (closeMenu && _activator != null)
                _activator.Close();
            if (closed != null)
                closed();
        }

        void LateUpdate()
        {
            if (_closed)
                return;
            if (!_centered)
            {
                _centered = true;
                CenterModal();
            }
            SnapTitle();
            RotatePreview();
            if (_preview != null)
                _preview.Tick();
            if (_baker != null)
                _baker.Tick();
            BlueprintPartPoint.SuppressPrompt();
            GameplayLock.Apply();
        }

        static SchematicScreen OpenShell(Action onClosed)
        {
            ScreenKit.ButtonTemplate();
            BuildMenuActivator activator = Object.FindAnyObjectByType<BuildMenuActivator>(FindObjectsInactive.Include);
            if (activator == null)
                return null;
            if (ScreenKit.Read<bool>(activator, "_isOpen"))
                activator.Close();
            activator.Open();
            GameObject menuObject = ScreenKit.Read<GameObject>(activator, "_buildMenu");
            if (menuObject == null)
                return null;
            BuildMenu menu = menuObject.GetComponent<BuildMenu>();
            if (menu == null)
            {
                activator.Close();
                return null;
            }

                menu.enabled = false;
            Image background = menuObject.GetComponentInChildren<Image>();
            GameColorPalette palette = ScreenKit.Read<GameColorPalette>(menu, "colorPalette");
            if (background != null && palette != null)
                background.color = palette.uiBackgroundColor;

            var screen = menuObject.AddComponent<SchematicScreen>();
            screen._activator = activator;
            screen._onClosed = onClosed;
            screen._holdingOpen = true;
            _openCount++;
            _active = screen;
            GameplayLock.SetWindow(true);
            screen.HidePointerDot();
            return screen;
        }

        // The panel of the build menu, emptied, and the part button that the ship cards are made from.
        RectTransform PrepareContent(out BuildPartButton buttonPrefab, out Vector2 area)
        {
            BuildMenu menu = GetComponent<BuildMenu>();
            Transform panel = ScreenKit.Read<Transform>(menu, "tabPanelContainer");
            buttonPrefab = ScreenKit.Read<BuildPartButton>(menu, "buttonPrefab");
            if (panel == null || buttonPrefab == null)
                throw new InvalidOperationException("The build menu has no tab panel or part button.");
            _panel = panel;
            ScreenKit.HideChrome(menu);
            ScreenKit.Clear(panel);
            ScreenKit.FreezeLayout(panel);
            Canvas.ForceUpdateCanvases();
            area = ScreenKit.PanelSize(panel);
            return ScreenKit.MakeContent(panel);
        }

        void FillLibrary(Action<SchematicFile> onPlace, Action<SchematicFile> onDelete)
        {
            _onPlace = onPlace;
            _onDelete = onDelete;
            RectTransform content = PrepareContent(out BuildPartButton buttonPrefab, out Vector2 area);
            TMP_FontAsset font = ScreenKit.FontFrom(buttonPrefab);
            RectTransform prefabRect = (RectTransform)buttonPrefab.transform;
            _cardPrefab = buttonPrefab;
            _cardWidth = prefabRect.rect.width < 20f ? 110f : prefabRect.rect.width;
            _cardHeight = prefabRect.rect.height < 20f ? 110f : prefabRect.rect.height;
            PlaceHeader("List of ships", font, true);
            float available = Mathf.Max(_cardWidth, area.x - Pad * 2f);
            _columns = Mathf.Max(1, Mathf.FloorToInt((available + Gap) / (_cardWidth + Gap)));
            _fittedWidth = (available - Gap * (_columns - 1)) / _columns;
            _cardScale = _fittedWidth / _cardWidth;
            _fittedHeight = _cardHeight * _cardScale;

            const float rowY = -12f;
            float rowH = ScreenKit.ButtonSize(SortCaptions[0]).y;
            float sortW = 0f;
            foreach (string caption in SortCaptions)
                sortW = Mathf.Max(sortW, ScreenKit.ButtonSize(caption).x);
            float clearW = rowH;
            float sortX = area.x - Pad - sortW;
            float clearX = sortX - Gap - clearW;
            float fieldW = Mathf.Max(180f, clearX - Gap - Pad);
            _filterInput = ScreenKit.AddNameField(content, _query, font, NameLimit);
            RectTransform fieldRect = _filterInput.GetComponent<RectTransform>();
            fieldRect.anchorMin = new Vector2(0f, 1f);
            fieldRect.anchorMax = new Vector2(0f, 1f);
            fieldRect.pivot = new Vector2(0f, 1f);
            fieldRect.anchoredPosition = new Vector2(Pad, rowY);
            fieldRect.sizeDelta = new Vector2(fieldW, rowH);
            foreach (Behaviour behaviour in fieldRect.GetComponents<Behaviour>())
            {
                if (behaviour is ContentSizeFitter || behaviour is LayoutElement)
                    behaviour.enabled = false;
            }
            if (_filterInput.placeholder is TMP_Text placeholder)
                placeholder.text = "Search";
            _filterInput.onValueChanged.AddListener(value =>
            {
                _query = value ?? "";
                ShowCards();
            });
            ScreenKit.PlaceSized(content, clearX, rowY, clearW, rowH, "X", ClearSearch);
            _sortButton = ScreenKit.PlaceSized(content, sortX, rowY, sortW, rowH, SortCaptions[(int)_sort], CycleSort);

            float buttonY = -area.y + ScreenKit.ButtonSize("Close").y + 20f;
            MakeCardArea(content, rowY - rowH - 16f, buttonY + 16f);
            ScreenKit.PlaceButton(content, Pad, buttonY, "Close", Close);
            float placeWidth = ScreenKit.ButtonSize("Place").x;
            float placeX = area.x - Pad - placeWidth;
            ScreenKit.PlaceButton(content, placeX, buttonY, "Place", PlaceSelected);
            const float ruleGap = 44f;
            ScreenKit.AddVerticalRule(content, placeX - ruleGap, buttonY, ScreenKit.ButtonSize("Place").y);
            ScreenKit.PlaceRight(content, placeX - ruleGap * 2f, buttonY, new[] { "Delete", "Rename" }, new Action[] { DeleteSelected, OpenRename });
            PaintDeleteRed();
            ScreenKit.PaintButton(transform, "Rename", new Color(0.92f, 0.58f, 0.08f, 1f));
            _baker = new ThumbnailBaker();
            ShowCards();
        }

        // The cards scroll between the search row and the buttons. The mask sits inside the window's nested canvas,
        // where it can clip the cards.
        void MakeCardArea(RectTransform content, float top, float bottom)
        {
            _viewport = ScreenKit.NewRect("Cards", content);
            _viewport.anchorMin = new Vector2(0f, 1f);
            _viewport.anchorMax = new Vector2(1f, 1f);
            _viewport.pivot = new Vector2(0f, 1f);
            _viewport.anchoredPosition = new Vector2(0f, top);
            _viewport.sizeDelta = new Vector2(0f, Mathf.Max(_fittedHeight + CardInset * 2f, top - bottom));
            _viewport.gameObject.AddComponent<RectMask2D>();
            _viewport.gameObject.AddComponent<ScrollArea>();
            _cardRoot = ScreenKit.NewRect("Card list", _viewport);
            _cardRoot.anchorMin = new Vector2(0f, 1f);
            _cardRoot.anchorMax = new Vector2(1f, 1f);
            _cardRoot.pivot = new Vector2(0f, 1f);
            _cardRoot.anchoredPosition = Vector2.zero;
            _cardRoot.sizeDelta = Vector2.zero;
            ScrollRect scroll = _viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = _cardRoot;
            scroll.viewport = _viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = (_fittedHeight + Gap) * 0.5f / WheelStep();
        }

        // The game's UI input module turns one wheel notch into scrollDeltaPerTick.
        static float WheelStep()
        {
            EventSystem events = EventSystem.current;
            InputSystemUIInputModule module = events == null ? null : events.currentInputModule as InputSystemUIInputModule;
            return module != null && module.scrollDeltaPerTick > 0.01f ? module.scrollDeltaPerTick : 1f;
        }

        // Lays out the cards for the current search and sort. Cards are made once per ship and reused.
        void ShowCards(int preferIndex = 0)
        {
            if (_cardRoot == null || _cardPrefab == null)
                return;
            DropDeletedCards();
            List<SchematicFile> files = VisibleFiles();
            _shown.Clear();
            var visible = new HashSet<ShipCard>();
            for (int i = 0; i < files.Count; i++)
            {
                ShipCard card = CardFor(files[i]);
                int column = i % _columns;
                int row = i / _columns;
                card.Rect.anchoredPosition = new Vector2(Pad + column * (_fittedWidth + Gap), -CardInset - row * (_fittedHeight + Gap));
                if (!card.Rect.gameObject.activeSelf)
                    card.Rect.gameObject.SetActive(true);
                _shown.Add(card);
                visible.Add(card);
            }
            foreach (ShipCard card in _cards.Values)
            {
                if (!visible.Contains(card) && card.Rect.gameObject.activeSelf)
                    card.Rect.gameObject.SetActive(false);
            }
            int rows = (_shown.Count + _columns - 1) / _columns;
            float height = rows == 0 ? 0f : CardInset * 2f + rows * _fittedHeight + (rows - 1) * Gap;
            _cardRoot.sizeDelta = new Vector2(0f, height);

            ShipCard keep = _selected != null && visible.Contains(_selected) ? _selected : null;
            if (keep == null && _shown.Count > 0)
                keep = _shown[Mathf.Clamp(preferIndex, 0, _shown.Count - 1)];
            Select(keep, true);
        }

        void DropDeletedCards()
        {
            var present = new HashSet<string>();
            foreach (SchematicFile entry in SchematicLibrary.Entries)
            {
                if (entry != null && entry.id != null)
                    present.Add(entry.id);
            }
            var gone = new List<string>();
            foreach (KeyValuePair<string, ShipCard> pair in _cards)
            {
                if (!present.Contains(pair.Key))
                    gone.Add(pair.Key);
            }
            foreach (string id in gone)
            {
                ShipCard card = _cards[id];
                if (_selected == card)
                    _selected = null;
                card.Remove();
                _cards.Remove(id);
            }
        }

        List<SchematicFile> VisibleFiles()
        {
            var files = new List<SchematicFile>();
            string query = (_query ?? "").Trim();
            foreach (SchematicFile entry in SchematicLibrary.Entries)
            {
                if (entry == null || entry.id == null)
                    continue;
                if (query.Length > 0 && (entry.name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                files.Add(entry);
            }
            files.Sort(CompareFiles);
            return files;
        }

        int CompareFiles(SchematicFile left, SchematicFile right)
        {
            int order;
            if (_sort == SortOrder.Newest)
                order = string.Compare(right.savedUtc, left.savedUtc, StringComparison.Ordinal);
            else if (_sort == SortOrder.Size)
                order = right.partCount.CompareTo(left.partCount);
            else
                order = 0;
            if (order != 0)
                return order;
            order = string.Compare(left.name, right.name, StringComparison.OrdinalIgnoreCase);
            if (order != 0)
                return order;
            return string.Compare(left.id, right.id, StringComparison.Ordinal);
        }

        ShipCard CardFor(SchematicFile file)
        {
            if (_cards.TryGetValue(file.id, out ShipCard card))
            {
                card.Refresh(file);
                return card;
            }
            card = new ShipCard { File = file };
            ShipCard clicked = card;
            card.Button = ScreenKit.MakeCard(_cardRoot, _cardPrefab, file.name, () => ClickCard(clicked), out card.Label);
            card.Rect = card.Button.GetComponent<RectTransform>();
            card.Rect.anchorMin = new Vector2(0f, 1f);
            card.Rect.anchorMax = new Vector2(0f, 1f);
            card.Rect.pivot = new Vector2(0f, 1f);
            card.Rect.sizeDelta = new Vector2(_cardWidth, _cardHeight);
            card.Rect.localScale = new Vector3(_cardScale, _cardScale, 1f);
            card.SetPicture(SchematicLibrary.LoadPreview(file));
            if (SchematicLibrary.PreviewLooksEmpty(card.Picture))
                _baker.Add(card);
            _cards[file.id] = card;
            return card;
        }

        void ClickCard(ShipCard card)
        {
            if (_renameDialog != null)
                return;
            float now = Time.unscaledTime;
            bool again = card == _lastClicked && now - _lastClickTime <= DoubleClick;
            _lastClicked = card;
            _lastClickTime = now;
            Select(card, false);
            if (again)
                PlaceSelected();
        }

        void Select(ShipCard card, bool reveal)
        {
            if (_selected != null && _selected != card)
                ScreenKit.SetCardSelected(_selected.Button, false);
            _selected = card;
            if (card == null)
                return;
            ScreenKit.SetCardSelected(card.Button, true);
            if (reveal)
                Reveal(card);
        }

        // Scrolls just enough to show the card. Clicks do not scroll, so the second click of a double click lands
        // on the same card.
        void Reveal(ShipCard card)
        {
            int index = _shown.IndexOf(card);
            if (index < 0 || _viewport == null)
                return;
            float top = CardInset + (index / _columns) * (_fittedHeight + Gap);
            float bottom = top + _fittedHeight;
            float view = _viewport.rect.height;
            Vector2 position = _cardRoot.anchoredPosition;
            if (top - CardInset < position.y)
                position.y = top - CardInset;
            else if (bottom + CardInset > position.y + view)
                position.y = bottom + CardInset - view;
            position.y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, _cardRoot.sizeDelta.y - view));
            _cardRoot.anchoredPosition = position;
        }

        SchematicFile Current()
        {
            return _selected != null && _selected.Alive ? _selected.File : null;
        }

        void PlaceSelected()
        {
            SchematicFile file = Current();
            if (file != null && _renameDialog == null && _onPlace != null)
                _onPlace(file);
        }

        void CycleSort()
        {
            if (_renameDialog != null)
                return;
            _sort = (SortOrder)(((int)_sort + 1) % SortCaptions.Length);
            ScreenKit.SetLabel(_sortButton, SortCaptions[(int)_sort]);
            ShowCards();
        }

        void ClearSearch()
        {
            if (_filterInput == null || _renameDialog != null)
                return;
            _filterInput.text = "";
            _filterInput.ActivateInputField();
        }

        void DeleteSelected()
        {
            SchematicFile file = Current();
            if (file == null || _onDelete == null || _renameDialog != null)
                return;
            int index = _shown.IndexOf(_selected);
            _onDelete(file);
            ShowCards(index);
        }

        void ImportShips()
        {
            if (_renameDialog != null)
                return;
            ExchangeReport report = SchematicExchange.PickAndImport();
            if (report == null)
                return;
            Notice(report.Message);
            if (!report.LibraryChanged || _closed)
                return;
            foreach (ShipCard card in _cards.Values)
            {
                if (card.Picture == null)
                    card.SetPicture(SchematicLibrary.LoadPreview(card.File));
            }
            ShowCards();
            if (report.SelectId != null && _cards.TryGetValue(report.SelectId, out ShipCard imported) && _shown.Contains(imported))
                Select(imported, true);
        }

        void ExportShip()
        {
            if (_renameDialog == null)
                Notice(SchematicExchange.PickAndExport(Current()));
        }

        void Notice(string message)
        {
            if (!string.IsNullOrEmpty(message) && _onNotice != null)
                _onNotice(message);
        }

        void FillSave(IShipState ship, SchematicFile file, string suggestedName, bool alreadySaved, Action<string, byte[], bool> onSave)
        {
            RectTransform content = PrepareContent(out BuildPartButton buttonPrefab, out Vector2 area);
            TMP_FontAsset font = ScreenKit.FontFrom(buttonPrefab);
            _preview = new SchematicPreview(SavePictureSize);
            _preview.Show(ship, file);
            PlaceHeader("Save ship", font, false);
            Vector2 buttonSize = ScreenKit.ButtonSize("Save as new");
            _nameInput = ScreenKit.AddNameField(content, suggestedName, font, NameLimit);
            RectTransform nameRect = _nameInput.GetComponent<RectTransform>();
            float fieldHeight = nameRect.sizeDelta.y > 24f ? nameRect.sizeDelta.y : nameRect.rect.height;
            if (fieldHeight < 48f)
                fieldHeight = 72f;
            const float gap = 18f;
            float chrome = fieldHeight + buttonSize.y + gap * 3f;
            float side = Mathf.Min(area.x - 48f, Mathf.Max(220f, area.y - chrome));

            _picture = ScreenKit.NewRect("Preview", content);
            _picture.anchorMin = new Vector2(0f, 1f);
            _picture.anchorMax = new Vector2(0f, 1f);
            _picture.pivot = new Vector2(0f, 1f);
            _picture.anchoredPosition = new Vector2((area.x - side) * 0.5f, -12f);
            _picture.sizeDelta = new Vector2(side, side);
            Image backdrop = _picture.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.04f, 0.07f, 0.1f, 1f);
            backdrop.raycastTarget = false;
            RectTransform imageRect = ScreenKit.NewRect("Picture", _picture);
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = new Vector2(8f, 8f);
            imageRect.offsetMax = new Vector2(-8f, -8f);
            RawImage image = imageRect.gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.texture = _preview.Texture;

            float nameY = -12f - side - gap;
            nameRect.anchorMin = new Vector2(0f, 1f);
            nameRect.anchorMax = new Vector2(0f, 1f);
            nameRect.pivot = new Vector2(0f, 1f);
            nameRect.anchoredPosition = new Vector2(16f, nameY);
            nameRect.sizeDelta = new Vector2(Mathf.Max(240f, area.x - 32f), fieldHeight);

            float buttonY = nameY - fieldHeight - gap;
            ScreenKit.PlaceButton(content, 20f, buttonY, "Cancel", Close);
            if (alreadySaved)
            {
                ScreenKit.PlaceRight(content, area.x, buttonY, new[] { "Overwrite", "Save as new" }, new Action[]
                {
                    () => onSave(ReadName(), _preview.CapturePng(), false),
                    () => onSave(ReadName(), _preview.CapturePng(), true)
                });
            }
            else
            {
                ScreenKit.PlaceRight(content, area.x, buttonY, new[] { "Save" }, new Action[]
                {
                    () => onSave(ReadName(), _preview.CapturePng(), true)
                });
            }
        }

        string ReadName()
        {
            string name = _nameInput == null ? "" : (_nameInput.text ?? "").Trim();
            return name.Length > NameLimit ? name.Substring(0, NameLimit) : name;
        }

        // The preview turns while a mouse button that was pressed on the picture is held.
        void RotatePreview()
        {
            Mouse mouse = Mouse.current;
            if (_preview == null || _picture == null || mouse == null)
                return;
            if (!mouse.leftButton.isPressed && !mouse.rightButton.isPressed)
            {
                _rotating = false;
                return;
            }
            if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
                _rotating = PointerOver(_picture, mouse.position.ReadValue());
            if (!_rotating)
                return;
            Vector2 delta = mouse.delta.ReadValue();
            _preview.Rotate(delta.x * 0.35f, -delta.y * 0.2f);
        }

        static bool PointerOver(RectTransform rect, Vector2 screenPoint)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Canvas root = canvas == null ? null : canvas.rootCanvas;
            Camera eye = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, eye);
        }

        void OpenRename()
        {
            SchematicFile file = Current();
            if (file == null || _renameDialog != null)
                return;
            GameObject template = ScreenKit.RenameTemplate;
            if (template == null)
                return;

            _renameOverlay = new GameObject("Rename ship canvas");
            Canvas overlayCanvas = _renameOverlay.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 1000;
            _renameOverlay.AddComponent<GraphicRaycaster>();
            Canvas host = GetComponentInParent<Canvas>();
            CanvasScaler sourceScaler = host != null ? host.rootCanvas.GetComponent<CanvasScaler>() : null;
            if (sourceScaler != null)
            {
                CanvasScaler scaler = _renameOverlay.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = sourceScaler.uiScaleMode;
                scaler.referenceResolution = sourceScaler.referenceResolution;
                scaler.screenMatchMode = sourceScaler.screenMatchMode;
                scaler.matchWidthOrHeight = sourceScaler.matchWidthOrHeight;
            }
            Object.DontDestroyOnLoad(_renameOverlay);
            GameObject dialog = Object.Instantiate(template, _renameOverlay.transform);
            dialog.name = "Rename ship";
            dialog.SetActive(false);
            ScreenKit.DisableWorldModal(dialog);
            _renameDialog = dialog;

            TMP_InputField field = ScreenKit.NameFieldOf(dialog);
            _renameField = field;
            Button confirm = ScreenKit.ConfirmButton(dialog);
            Button cancel = ScreenKit.CancelButton(dialog, confirm);
            ScreenKit.SilenceLocalization(dialog);
            ScreenKit.SetDialogTitle(dialog, field, "Rename ship");
            ScreenKit.Quiet(confirm);
            ScreenKit.Quiet(cancel);
            ScreenKit.SetButtonLabel(confirm, "Save");
            ScreenKit.SetButtonLabel(cancel, "Cancel");
            if (confirm != null)
                confirm.onClick.AddListener(() => ApplyRename(file, field));
            if (cancel != null)
                cancel.onClick.AddListener(CloseRename);
            if (field != null)
            {
                field.characterLimit = NameLimit;
                field.onSubmit.AddListener(_ => ApplyRename(file, field));
            }
            dialog.SetActive(true);
            Canvas.ForceUpdateCanvases();
            ScreenKit.CenterRect(dialog.GetComponent<RectTransform>());
            if (field != null)
            {
                field.text = file.name;
                field.Select();
                field.ActivateInputField();
            }
        }

        void ApplyRename(SchematicFile file, TMP_InputField field)
        {
            string name = field == null ? "" : (field.text ?? "").Trim();
            if (name.Length == 0)
                return;
            if (SchematicLibrary.Entries.Contains(file))
                SchematicLibrary.Rename(file, name);
            CloseRename();
            ShowCards();
        }

        void CloseRename()
        {
            if (_renameOverlay != null)
                Object.Destroy(_renameOverlay);
            else if (_renameDialog != null)
                Object.Destroy(_renameDialog);
            _renameDialog = null;
            _renameOverlay = null;
            _renameField = null;
        }

        void HidePointerDot()
        {
            foreach (Transform item in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (item.name != "Crosshair" || !item.gameObject.activeSelf)
                    continue;
                item.gameObject.SetActive(false);
                _hiddenDots.Add(item.gameObject);
            }
        }

        void ShowPointerDot()
        {
            foreach (GameObject dot in _hiddenDots)
            {
                if (dot != null)
                    dot.SetActive(true);
            }
            _hiddenDots.Clear();
        }

        void PaintDeleteRed()
        {
            GameColorPalette palette = ScreenKit.Read<GameColorPalette>(GetComponent<BuildMenu>(), "colorPalette");
            Color bad = palette != null ? palette.uiBadButtonBackgroundColor : new Color(0.62f, 0.16f, 0.18f, 1f);
            ScreenKit.PaintButton(transform, "Delete", bad);
        }

        void FindWindow()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            _canvasRect = canvas != null ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            _window = _canvasRect != null && _panel != null ? ScreenKit.WindowRoot(_panel, _canvasRect) : transform as RectTransform;
        }

        void CenterModal()
        {
            if (_window == null || _canvasRect == null)
                FindWindow();
            if (_window != null && _canvasRect != null)
                ScreenKit.NudgeToCenter(_window, _canvasRect);
        }

        void PlaceHeader(string label, TMP_FontAsset font, bool cornerButtons)
        {
            FindWindow();
            _separator = ScreenKit.FindSeparator(_window);
            Transform parent = _window != null ? _window : transform;
            if (_title != null)
                Object.Destroy(_title.gameObject);
            _title = ScreenKit.AddTitle(parent, label, font);
            if (cornerButtons)
            {
                if (_importButton == null)
                    _importButton = ScreenKit.MakeIconButton(parent, "Schematic import", false, ImportShips);
                if (_exportButton == null)
                    _exportButton = ScreenKit.MakeIconButton(parent, "Schematic export", true, ExportShip);
            }
            _snapped.x = float.NaN;
            SnapTitle();
        }

        // The title sits between the top of the frame and the line under it, like the game's own window titles.
        // It is moved only when the frame or the line moved, so the menu canvas is not rebuilt every frame.
        void SnapTitle()
        {
            if (_title == null || _panel == null)
                return;
            if (_window == null)
                FindWindow();
            if (_window == null)
                return;
            if (_separator == null && _separatorTries < 30)
            {
                _separatorTries++;
                _separator = ScreenKit.FindSeparator(_window);
            }
            _window.GetWorldCorners(Frame);
            float frameTop = Frame[1].y;
            float lineTop = frameTop;
            if (_separator != null)
            {
                _separator.GetWorldCorners(Line);
                lineTop = Line[1].y;
            }
            var snap = new Vector4(Frame[0].x, frameTop, Frame[2].x, lineTop);
            float scaleX = Mathf.Abs(_window.lossyScale.x);
            if (snap == _snapped && scaleX == _snappedScale)
                return;
            _snapped = snap;
            _snappedScale = scaleX;

            float gap = Mathf.Max(0f, frameTop - lineTop);
            float y = lineTop + gap * 0.42f;
            float pad = 118f * scaleX;
            _title.position = new Vector3(Frame[0].x + pad, y, Frame[0].z);
            if (_importButton == null || _exportButton == null)
                return;
            float scaleY = Mathf.Abs(_window.lossyScale.y);
            if (scaleY < 0.0001f)
                scaleY = 1f;
            if (scaleX < 0.0001f)
                scaleX = 1f;
            float side = Mathf.Clamp(gap / scaleY * 0.62f, 36f, 58f);
            float right = Frame[2].x - pad;
            PlaceCorner(_exportButton, right, y, Frame[0].z, side);
            PlaceCorner(_importButton, right - (side + 8f) * scaleX, y, Frame[0].z, side);
        }

        static void PlaceCorner(RectTransform rect, float right, float y, float z, float side)
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(side, side);
            rect.position = new Vector3(right, y, z);
        }
    }
}
