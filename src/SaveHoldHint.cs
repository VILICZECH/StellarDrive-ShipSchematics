using System;
using HarmonyLib;
using Managers.Client.Services;
using MelonLoader;
using TMPro;
using UI.Common;
using UI.Hints;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // A copy of the game's "hold" hint that fills while P is held to save the ship.
    sealed class SaveHoldHint
    {
        static readonly AccessTools.FieldRef<HintsActivator, HintsCanvas> Prefab =
            AccessTools.FieldRefAccess<HintsActivator, HintsCanvas>("hintsCanvasPrefab");
        static readonly AccessTools.FieldRef<HintsActivator, Transform> Parent =
            AccessTools.FieldRefAccess<HintsActivator, Transform>("repositionTipCanvasParent");
        static readonly AccessTools.FieldRef<HintsCanvas, CanvasGroup> Hold =
            AccessTools.FieldRefAccess<HintsCanvas, CanvasGroup>("hold");
        static readonly AccessTools.FieldRef<HintsCanvas, CanvasGroup> ThrustHint =
            AccessTools.FieldRefAccess<HintsCanvas, CanvasGroup>("thrustHint");
        static readonly AccessTools.FieldRef<HintsCanvas, CanvasGroup> PlaceKeys =
            AccessTools.FieldRefAccess<HintsCanvas, CanvasGroup>("placeKeys");

        HintsCanvas _hint;
        bool _broken;

        public void Tick(bool show, float fill)
        {
            if (_broken)
                return;
            try
            {
                if (!show)
                {
                    FadeOut();
                    return;
                }
                if (_hint == null)
                    _hint = Create();
                if (_hint == null)
                    return;
                _hint.FadeInHold();
                _hint.UpdateFill(fill);
            }
            catch (Exception ex)
            {
                _broken = true;
                Destroy();
                MelonLogger.Warning("The hold hint for saving is off until the game restarts. " + ex.Message);
            }
        }

        public void Destroy()
        {
            if (_hint != null)
                Object.Destroy(_hint.gameObject);
            _hint = null;
        }

        void FadeOut()
        {
            if (_hint == null)
                return;
            _hint.EnsureIsFadingOut();
            if (_hint.IsDoneFadingOut)
                Destroy();
        }

        static HintsCanvas Create()
        {
            HintsActivator activator = GameServices.Find<HintsActivator>();
            if (activator == null)
                return null;
            HintsCanvas prefab = Prefab(activator);
            Transform parent = Parent(activator);
            if (prefab == null || parent == null)
                return null;

            HintsCanvas clone = Object.Instantiate(prefab, parent);
            clone.name = "Schematic hold hint";
            foreach (GraphicRaycaster raycaster in clone.GetComponents<GraphicRaycaster>())
                raycaster.enabled = false;
            Hide(ThrustHint(clone));
            Hide(PlaceKeys(clone));
            CanvasGroup hold = Hold(clone);
            if (hold != null)
            {
                RectTransform holdRect = hold.GetComponent<RectTransform>();
                if (holdRect != null)
                {
                    LayoutElement element = holdRect.GetComponent<LayoutElement>();
                    if (element == null)
                        element = holdRect.gameObject.AddComponent<LayoutElement>();
                    element.ignoreLayout = true;
                    holdRect.anchoredPosition += new Vector2(0f, -160f);
                }
                ReplaceKeyLabel(hold.gameObject);
            }
            return clone;
        }

        static void Hide(CanvasGroup group)
        {
            if (group != null)
                group.gameObject.SetActive(false);
        }

        static void ReplaceKeyLabel(GameObject root)
        {
            foreach (LocalizeStringEvent localize in root.GetComponentsInChildren<LocalizeStringEvent>(true))
                localize.enabled = false;
            foreach (KeyTipStringUpdater updater in root.GetComponentsInChildren<KeyTipStringUpdater>(true))
                updater.enabled = false;
            foreach (KeyTipIcon icon in root.GetComponentsInChildren<KeyTipIcon>(true))
                icon.enabled = false;
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!string.IsNullOrEmpty(text.text))
                    text.text = KeyLabel(text.text);
            }
            foreach (Text text in root.GetComponentsInChildren<Text>(true))
            {
                if (!string.IsNullOrEmpty(text.text))
                    text.text = KeyLabel(text.text);
            }
        }

        static string KeyLabel(string old)
        {
            return old.Trim().Length <= 2 ? "P" : "Save ship (hold)";
        }
    }
}
