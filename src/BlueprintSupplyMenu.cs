using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using HarmonyLib;
using Items.Interface.Service;
using Items.Model;
using MelonLoader;
using StellarDriveMods.Shared;
using TMPro;
using UI.Common;
using UI.Interface.Model;
using UI.Inventory;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // Survival blueprint window: a clone of the game's storage menu whose chest slots are material bins.
    public sealed class BlueprintSupplyMenu : MonoBehaviour, IClosableMenu
    {
        static BlueprintSupplyMenu _current;
        static AccessTools.FieldRef<InventoryGrid, InventoryType> _gridSource;
        static AccessTools.FieldRef<InventorySlotUI, TMP_Text> _slotQuantity;
        static AccessTools.FieldRef<InventoryMouseActionsTracker, InventorySlot?> _hold;
        static AccessTools.FieldRef<InventoryMouseActionsTracker, bool> _splitHeld;
        static AccessTools.FieldRef<StorageMenuActivator, GameObject> _smallStorage;
        static AccessTools.FieldRef<StorageMenuActivator, GameObject> _woodThruster;
        static bool _fieldsBound;
        static bool _fieldsOk;
        static readonly Vector3[] Corners = new Vector3[4];

        SchematicBuildSite _host;
        RectTransform _frame;
        RectTransform _window;
        Sprite _frameSprite;
        Sprite _fillSprite;
        Color _fillColor = new Color(0.07f, 0.16f, 0.28f, 1f);
        RectTransform _missingRoot;
        readonly List<TMP_Text> _missingLines = new List<TMP_Text>();
        readonly List<RawImage> _missingIcons = new List<RawImage>();
        readonly List<SchematicBuildSite.MaterialNeed> _needs = new List<SchematicBuildSite.MaterialNeed>();
        InventorySlotUI[] _slots = Array.Empty<InventorySlotUI>();
        uint[] _shownIds = Array.Empty<uint>();
        int[] _shownQty = Array.Empty<int>();
        Vector3 _lastWindow;
        Vector2 _lastScreen;
        bool _openedInventory;
        bool _closed;
        bool _placed;

        public static BlueprintSupplyMenu Current => _current;

        public static void Open(SchematicBuildSite host)
        {
            CloseCurrent();
            if (host == null || !host.HasWork || !BindFields())
                return;

            StorageMenuActivator activator = GameServices.Find<StorageMenuActivator>();
            if (activator == null)
            {
                MelonLogger.Warning("The storage menu is not available.");
                return;
            }

            GameObject prefab = Prefab(activator);
            if (prefab == null)
            {
                MelonLogger.Warning("The storage menu prefab is missing.");
                return;
            }

            GameObject menu = Object.Instantiate(prefab, activator.transform);
            menu.name = "Blueprint";
            BlueprintSupplyMenu window = menu.AddComponent<BlueprintSupplyMenu>();
            window._host = host;
            try
            {
                window.Prepare(menu);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not open the blueprint menu.", ex);
                window.Close();
                return;
            }
            MenuTracker tracker = GameServices.Get<MenuTracker>();
            if (tracker != null)
                tracker.TrackMenu(window);
            GameplayLock.SetLook(true);
            _current = window;
        }

        public static void CloseCurrent()
        {
            if (_current != null)
                _current.Close();
        }

        public void Close()
        {
            if (_closed)
                return;
            _closed = true;
            if (_current == this)
                _current = null;
            MenuTracker tracker = GameServices.Get<MenuTracker>();
            if (tracker != null)
                tracker.UntrackMenu(this);
            GameplayLock.SetLook(false);
            if (_missingRoot != null)
                Destroy(_missingRoot.gameObject);
            if (_openedInventory)
            {
                _openedInventory = false;
                var closer = new GameObject("Schematic inventory closer");
                closer.AddComponent<InventoryCloser>();
            }
            if (this != null)
                Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (!_closed)
                Close();
        }

        void LateUpdate()
        {
            if (_closed)
                return;
            BlueprintPartPoint.SuppressPrompt();
            PlaceSides();
            if (_host == null || !_host.HasWork)
            {
                Close();
                return;
            }
            MenuTracker tracker = GameServices.Get<MenuTracker>();
            if (tracker == null
                && GameKeys.FreeInOwnWindow()
                && Keyboard.current != null
                && Keyboard.current.escapeKey.wasPressedThisFrame)
                Close();
        }

        void Prepare(GameObject menu)
        {
            InventoryGrid[] grids = GetComponentsInChildren<InventoryGrid>(true);
            bool hasInventory = false;
            var chestSlots = new List<InventorySlotUI>();
            for (int i = 0; i < grids.Length; i++)
            {
                InventoryType kind = _gridSource(grids[i]);
                if (kind == InventoryType.Chest)
                {
                    InventorySlotUI[] slots = grids[i].GetComponentsInChildren<InventorySlotUI>(true);
                    for (int s = 0; s < slots.Length; s++)
                        chestSlots.Add(slots[s]);
                    grids[i].enabled = false;
                }
                else if (kind == InventoryType.MainInventory)
                {
                    hasInventory = true;
                }
            }
            if (chestSlots.Count == 0)
            {
                InventorySlotUI[] slots = GetComponentsInChildren<InventorySlotUI>(true);
                for (int i = 0; i < slots.Length; i++)
                    chestSlots.Add(slots[i]);
            }
            _slots = chestSlots.ToArray();
            _shownIds = new uint[_slots.Length];
            _shownQty = new int[_slots.Length];
            for (int i = 0; i < _shownQty.Length; i++)
                _shownQty[i] = int.MinValue;
            _host.EnsureBins(_slots.Length);
            for (int i = 0; i < _slots.Length; i++)
            {
                BinMark mark = _slots[i].GetComponent<BinMark>();
                if (mark == null)
                    mark = _slots[i].gameObject.AddComponent<BinMark>();
                mark.Index = i;
                mark.Menu = this;
            }
            ShowBins();

            _frame = LargestFrame(menu);
            _window = _frame;
            RememberFrame(_frame);
            RenameTitle(menu, _host.ShipName);
            transform.localScale = new Vector3(0.86f, 0.86f, 1f);
            RebuildMissing();
            if (!hasInventory)
                _openedInventory = OpenInventory();
            PlaceSides();
        }

        static void RenameTitle(GameObject menu, string shipName)
        {
            TMP_Text title = Named<TMP_Text>(menu, "ShipPartName");
            if (title == null)
            {
                TMP_Text[] texts = menu.GetComponentsInChildren<TMP_Text>(true);
                float best = -1f;
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i].GetComponentInParent<InventorySlotUI>(true) != null)
                        continue;
                    string value = texts[i].text ?? "";
                    bool named = value.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0
                        || value.IndexOf("Wood-Powered", StringComparison.OrdinalIgnoreCase) >= 0
                        || value.IndexOf("Wood Powered", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (named)
                    {
                        title = texts[i];
                        break;
                    }
                    if (texts[i].fontSize > best)
                    {
                        best = texts[i].fontSize;
                        title = texts[i];
                    }
                }
            }
            if (title == null)
                return;
            ScreenKit.SilenceLocalization(title.gameObject);
            title.text = "Blueprint " + shipName;
            title.textWrappingMode = TextWrappingModes.NoWrap;
            title.overflowMode = TextOverflowModes.Ellipsis;
            RenameSlot(menu);
        }

        static void RenameSlot(GameObject menu)
        {
            TMP_Text label = Named<TMP_Text>(menu, "ItemName");
            if (label == null)
            {
                TMP_Text[] texts = menu.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if ((texts[i].text ?? "").Trim() == "Wood")
                    {
                        label = texts[i];
                        break;
                    }
                }
            }
            if (label != null)
            {
                ScreenKit.SilenceLocalization(label.gameObject);
                label.text = "Place here";
                label.fontSize = 16f;
            }
            HideMass(menu);
        }

        static void HideMass(GameObject menu)
        {
            Transform named = Named<Transform>(menu, "MassIndicator");
            if (named != null)
            {
                named.gameObject.SetActive(false);
                return;
            }
            TMP_Text[] texts = menu.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                if ((texts[i].text ?? "").IndexOf("Mass", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                texts[i].gameObject.SetActive(false);
            }
        }

        static T Named<T>(GameObject root, string name) where T : Component
        {
            T[] all = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.name == name)
                    return all[i];
            }
            return null;
        }

        static RectTransform LargestFrame(GameObject menu)
        {
            RectTransform best = menu.transform as RectTransform;
            float bestArea = 0f;
            Image[] images = menu.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].GetComponentInParent<InventorySlotUI>(true) != null)
                    continue;
                RectTransform rect = images[i].rectTransform;
                float area = Mathf.Abs(rect.rect.width * rect.rect.height);
                if (area > bestArea)
                {
                    best = rect;
                    bestArea = area;
                }
            }
            return best;
        }

        void RememberFrame(RectTransform frame)
        {
            Image frameImage = frame != null ? frame.GetComponent<Image>() : null;
            if (frameImage != null && frameImage.sprite != null)
                _frameSprite = frameImage.sprite;

            Image[] images = GetComponentsInChildren<Image>(true);
            Image fill = null;
            float best = 0f;
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] == frameImage)
                    continue;
                if (images[i].GetComponentInParent<InventorySlotUI>(true) != null)
                    continue;
                Rect rect = images[i].rectTransform.rect;
                float area = Mathf.Abs(rect.width * rect.height);
                if (area > best)
                {
                    fill = images[i];
                    best = area;
                }
            }
            if (fill == null)
                return;
            _fillSprite = fill.sprite;
            _fillColor = fill.color;
        }

        public void AcceptDrop(InventoryMouseActionsTracker tracker)
        {
            if (_closed || _host == null || !TryReadHold(tracker, out uint itemId, out int quantity))
                return;
            int index = HoveredBin();
            if (index < 0)
                return;
            int existing = _host.BinOf(itemId);
            if (existing >= 0)
                index = existing;
            if (!_host.CanAccept(index, itemId))
                return;
            int taken = _host.PullItems(itemId, quantity);
            if (taken <= 0)
                return;
            _host.PutBin(index, itemId, taken);
            if (_closed)
                return;
            ShowBins();
            RebuildMissing();
            tracker.NotifyStopDrag();
            _host.CompleteIfReady();
        }

        public void ReturnHoveredBin()
        {
            if (_closed || _host == null)
                return;
            int index = HoveredBin();
            if (index < 0)
                return;
            if (_host.TakeBin(index, out uint itemId, out int quantity))
                _host.ReturnItems(itemId, quantity);
            ShowBins();
            RebuildMissing();
        }

        void ShowBins()
        {
            if (_host == null)
                return;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] == null)
                    continue;
                SchematicBuildSite.Bin bin = _host.BinAt(i);
                if (_shownIds[i] == bin.ItemId && _shownQty[i] == bin.Quantity)
                    continue;
                _shownIds[i] = bin.ItemId;
                _shownQty[i] = bin.Quantity;
                if (bin.Quantity <= 0)
                {
                    _slots[i].UpdateSlot(default);
                    continue;
                }
                int shown = bin.Quantity;
                byte slotCount = (byte)(shown > 255 ? 255 : shown);
                _slots[i].UpdateSlot(new InventorySlot
                {
                    HasItem = true,
                    ItemId = bin.ItemId,
                    Quantity = slotCount
                });
                WriteCount(_slots[i], shown);
            }
        }

        static void WriteCount(InventorySlotUI slot, int count)
        {
            if (slot == null || count <= 1 || _slotQuantity == null)
                return;
            TMP_Text label = _slotQuantity(slot);
            if (label == null)
                return;
            label.text = count.ToString();
            label.enabled = true;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
        }

        void RebuildMissing()
        {
            if (_host == null)
                return;
            _host.CollectMissing(_needs);
            if (_missingRoot != null && _missingLines.Count == _needs.Count)
            {
                RefreshLists();
                return;
            }
            if (_missingRoot != null)
                Destroy(_missingRoot.gameObject);
            Canvas canvas = GetComponentInParent<Canvas>();
            Transform parent = canvas != null ? canvas.transform : transform;
            TMP_FontAsset font = SampleFont(gameObject);
            _missingLines.Clear();
            _missingIcons.Clear();
            _missingRoot = MakeList(parent, font);
            _placed = false;
            PlaceSides();
        }

        void RefreshLists()
        {
            IItemThumbnailProvider pictures = GameServices.Get<IItemThumbnailProvider>();
            for (int i = 0; i < _missingLines.Count; i++)
            {
                if (i >= _needs.Count)
                {
                    _missingLines[i].text = "";
                    if (i < _missingIcons.Count)
                        _missingIcons[i].texture = null;
                    continue;
                }
                _missingLines[i].text = _needs[i].Name + "  " + _needs[i].Required;
                if (i < _missingIcons.Count)
                    _missingIcons[i].texture = Thumbnail(pictures, _needs[i].ItemId);
            }
        }

        RectTransform MakeList(Transform parent, TMP_FontAsset font)
        {
            var root = new GameObject("Missing", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 1f);
            float height = 274f + _needs.Count * 78f;
            rect.sizeDelta = new Vector2(480f, height);

            Image plate = root.AddComponent<Image>();
            plate.raycastTarget = false;
            plate.color = Color.white;
            if (_frameSprite != null && SpriteCanSlice(_frameSprite))
            {
                plate.sprite = _frameSprite;
                plate.type = Image.Type.Sliced;
                plate.pixelsPerUnitMultiplier = 1f;
            }
            else
            {
                plate.color = new Color(0.62f, 0.68f, 0.72f, 1f);
            }

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(root.transform, false);
            RectTransform fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(28f, 28f);
            fillRect.offsetMax = new Vector2(-28f, -28f);
            Image fill = fillGo.AddComponent<Image>();
            fill.sprite = _fillSprite;
            fill.type = Image.Type.Simple;
            fill.color = _fillColor.a < 0.05f ? Color.white : _fillColor;
            fill.raycastTarget = false;

            IItemThumbnailProvider pictures = GameServices.Get<IItemThumbnailProvider>();
            float top = height * 0.5f - 96f;
            AddText(rect, font, "Missing", 32f, new Vector2(0f, top), FontStyles.Bold);
            float y = top - 92f;
            for (int i = 0; i < _needs.Count; i++)
            {
                TMP_Text line = AddText(rect, font, _needs[i].Name + "  " + _needs[i].Required, 28f, new Vector2(36f, y), FontStyles.Normal);
                line.alignment = TextAlignmentOptions.MidlineLeft;
                line.rectTransform.sizeDelta = new Vector2(250f, 44f);
                _missingLines.Add(line);
                RawImage icon = AddIcon(rect, new Vector2(-150f, y));
                icon.rectTransform.sizeDelta = new Vector2(52f, 52f);
                icon.texture = Thumbnail(pictures, _needs[i].ItemId);
                _missingIcons.Add(icon);
                y -= 74f;
            }
            return rect;
        }

        void PlaceSides()
        {
            if (_host == null || _missingRoot == null)
                return;
            if (_window == null)
                _window = _frame;
            if (_window == null)
                return;
            _window.GetWorldCorners(Corners);
            Vector3 topRight = Corners[2];
            Vector3 topLeft = Corners[1];
            Vector2 screen = new Vector2(Screen.width, Screen.height);
            if (_placed && topRight == _lastWindow && screen == _lastScreen)
                return;
            _placed = true;
            _lastWindow = topRight;
            _lastScreen = screen;
            float top = topLeft.y - 46f * Mathf.Abs(_window.lossyScale.y);
            float gap = WorldWidth(_missingRoot) * 0.55f + 28f * Mathf.Max(0.01f, _window.lossyScale.x);
            _missingRoot.position = new Vector3(topRight.x + gap, top, topLeft.z);
            _missingRoot.SetAsLastSibling();
        }

        static bool SpriteCanSlice(Sprite sprite)
        {
            if (sprite == null)
                return false;
            Vector4 border = sprite.border;
            return border.x > 8f && border.y > 8f && border.z > 8f && border.w > 8f;
        }

        static TMP_Text AddText(RectTransform parent, TMP_FontAsset font, string value, float size, Vector2 position, FontStyles style)
        {
            var go = new GameObject(value, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(200f, size + 8f);
            rect.anchoredPosition = position;
            TMP_Text text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
                text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        static RawImage AddIcon(RectTransform parent, Vector2 position)
        {
            var go = new GameObject("Icon", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(28f, 28f);
            rect.anchoredPosition = position;
            RawImage image = go.AddComponent<RawImage>();
            image.raycastTarget = false;
            return image;
        }

        static Texture Thumbnail(IItemThumbnailProvider pictures, uint itemId)
        {
            if (pictures == null)
                return null;
            try
            {
                return pictures.GetThumbnail(itemId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static TMP_FontAsset SampleFont(GameObject menu)
        {
            TMP_Text text = menu.GetComponentInChildren<TMP_Text>(true);
            return text == null ? null : text.font;
        }

        static bool TryReadHold(InventoryMouseActionsTracker tracker, out uint itemId, out int quantity)
        {
            itemId = 0;
            quantity = 0;
            if (tracker == null || _hold == null)
                return false;
            InventorySlot? held = _hold(tracker);
            if (!held.HasValue || !held.Value.HasItem || held.Value.Quantity <= 0)
                return false;
            itemId = held.Value.ItemId;
            quantity = held.Value.Quantity;
            if (_splitHeld != null && _splitHeld(tracker))
                quantity = (quantity + 1) / 2;
            return quantity > 0;
        }

        static bool OpenInventory()
        {
            PlayerInventoryActivator inventory = GameServices.Find<PlayerInventoryActivator>();
            if (inventory == null || inventory.IsInventoryActive)
                return false;
            inventory.ToggleInventory();
            return true;
        }

        static GameObject Prefab(StorageMenuActivator activator)
        {
            GameObject small = _smallStorage != null ? _smallStorage(activator) : null;
            if (small != null)
                return small;
            return _woodThruster != null ? _woodThruster(activator) : null;
        }

        public bool IsPointerOverBin()
        {
            return HoveredBin() >= 0;
        }

        public int HoveredBin()
        {
            if (EventSystem.current == null || Mouse.current == null)
                return -1;
            var data = new PointerEventData(EventSystem.current)
            {
                position = Mouse.current.position.ReadValue()
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject == null)
                    continue;
                BinMark mark = hits[i].gameObject.GetComponentInParent<BinMark>();
                if (mark != null && mark.Menu == this)
                    return mark.Index;
            }
            return -1;
        }

        public bool TryShiftFromPlayer(ItemSlotId slotId)
        {
            if (_closed || _host == null)
                return false;
            if (slotId.Type != InventoryType.MainInventory && slotId.Type != InventoryType.ToolBar)
                return true;
            if (NetworkManager.Instances.Count == 0 || !InstanceFinder.IsServerStarted)
                return false;
            InventorySlotProvider provider = GameServices.Get<InventorySlotProvider>();
            Players.Interface.Services.IPlayerInventoryOperationService operations = GameServices.Get<Players.Interface.Services.IPlayerInventoryOperationService>();
            if (provider == null || operations == null)
                return true;
            InventorySlot slot = provider.GetSlot(slotId);
            if (!slot.HasItem || slot.Quantity <= 0)
                return true;
            int index = _host.BinOf(slot.ItemId);
            if (index < 0)
                index = EmptyBin();
            if (index < 0 || !_host.CanAccept(index, slot.ItemId))
                return true;
            operations.DestroySlot(slotId);
            _host.PutBin(index, slot.ItemId, slot.Quantity);
            if (_closed)
                return true;
            ShowBins();
            RebuildMissing();
            _host.CompleteIfReady();
            return true;
        }

        int EmptyBin()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_host.BinAt(i).Quantity <= 0)
                    return i;
            }
            return -1;
        }

        static float WorldWidth(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Vector3.Distance(Corners[0], Corners[3]);
        }

        static bool BindFields()
        {
            if (_fieldsBound)
                return _fieldsOk;
            _fieldsBound = true;
            try
            {
                _gridSource = AccessTools.FieldRefAccess<InventoryGrid, InventoryType>("source");
                _slotQuantity = AccessTools.FieldRefAccess<InventorySlotUI, TMP_Text>("quantity");
                _hold = AccessTools.FieldRefAccess<InventoryMouseActionsTracker, InventorySlot?>("_currentHold");
                _splitHeld = AccessTools.FieldRefAccess<InventoryMouseActionsTracker, bool>("_splitCurrentlyHeld");
                _smallStorage = AccessTools.FieldRefAccess<StorageMenuActivator, GameObject>("smallStorageMenu");
                _woodThruster = AccessTools.FieldRefAccess<StorageMenuActivator, GameObject>("woodThrusterMenu");
                _fieldsOk = _gridSource != null && _slotQuantity != null && _hold != null && _splitHeld != null
                    && _smallStorage != null && _woodThruster != null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("The storage menu has changed; blueprint materials cannot be supplied. " + ex.Message);
                _fieldsOk = false;
            }
            if (!_fieldsOk)
                MelonLogger.Warning("The storage menu has changed; blueprint materials cannot be supplied.");
            return _fieldsOk;
        }
    }

    sealed class BinMark : MonoBehaviour
    {
        public int Index;
        public BlueprintSupplyMenu Menu;
    }

    // CloseAllOpenMenus walks the list backwards and UntrackMenu shrinks it; closing the inventory in the same call
    // can read past the end. The closer runs on the next frame, when that loop is done.
    sealed class InventoryCloser : MonoBehaviour
    {
        void LateUpdate()
        {
            PlayerInventoryActivator inventory = GameServices.Find<PlayerInventoryActivator>();
            if (inventory != null)
                inventory.Close();
            Destroy(gameObject);
        }
    }

    [HarmonyPatch(typeof(InventoryMouseActionsTracker), nameof(InventoryMouseActionsTracker.NotifyDrop))]
    static class BlueprintDropPatch
    {
        static bool Prefix(InventoryMouseActionsTracker __instance)
        {
            BlueprintSupplyMenu menu = BlueprintSupplyMenu.Current;
            if (menu == null || !menu.IsPointerOverBin())
                return true;
            try
            {
                menu.AcceptDrop(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not place the material.", ex);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryMouseActionsTracker), nameof(InventoryMouseActionsTracker.NotifyBeginMoveItem))]
    static class BlueprintTakeBackPatch
    {
        static bool Prefix()
        {
            BlueprintSupplyMenu menu = BlueprintSupplyMenu.Current;
            if (menu == null || !menu.IsPointerOverBin())
                return true;
            try
            {
                menu.ReturnHoveredBin();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not return the material.", ex);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryMouseActionsTracker), nameof(InventoryMouseActionsTracker.OnClick))]
    static class BlueprintShiftPatch
    {
        static bool Prefix(ItemSlotId itemSlot)
        {
            BlueprintSupplyMenu menu = BlueprintSupplyMenu.Current;
            if (menu == null || !ShiftHeld())
                return true;
            try
            {
                if (menu.HoveredBin() >= 0)
                    menu.ReturnHoveredBin();
                else if (!menu.TryShiftFromPlayer(itemSlot))
                    ScreenMessages.Status("Only the host can supply a blueprint.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not move the material.", ex);
            }
            return false;
        }

        static bool ShiftHeld()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        }
    }
}
