using System;
using System.Collections.Generic;
using MelonLoader;
using Players.Interface.Model;
using Players.Services;
using Ships.Parts.Common;
using Ships.Visuals;
using TMPro;
using UI.Common;
using UI.Interaction;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.SceneManagement;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;

namespace ShipSchematics
{
    // The interactable box of a blueprint. It lives in the client physics scene, so the game's interaction ray finds
    // it, and opens the material window.
    public sealed class BlueprintPartPoint : MonoBehaviour, IInteractableObject
    {
        static readonly List<PartInteractionConfig> Empty = new List<PartInteractionConfig>();
        static List<PartInteractionConfig> _interactions;

        SchematicBuildSite _host;
        Transform _visual;
        BlueprintRecycleTarget _recycle;
        int _labelledCanvas;

        public SchematicBuildSite Host => _host;

        public BlueprintRecycleTarget RecycleTarget
        {
            get
            {
                if (_recycle == null && _host != null)
                    _recycle = new BlueprintRecycleTarget(_host);
                return _recycle;
            }
        }

        public bool IsActive => _host != null && _host.HasWork && !SchematicPlacer.Busy;

        public float DetectionDistance => 2f;

        public IReadOnlyCollection<PartInteractionConfig> Interactions => _interactions == null || _interactions.Count == 0 ? Empty : _interactions;

        public static BlueprintPartPoint Attach(GameObject volume, SchematicBuildSite host, ShipPartVisuals hologram)
        {
            EnsureLabel(hologram);
            int layer = LayerMask.NameToLayer("Interactable");
            if (layer < 0)
                layer = LayerMask.NameToLayer("Default");
            if (layer >= 0)
                volume.layer = layer;
            try
            {
                volume.tag = "Interactable";
            }
            catch (UnityException)
            {
                MelonLogger.Warning("The Interactable tag is missing. The build prompt cannot be shown.");
            }
            var point = volume.AddComponent<BlueprintPartPoint>();
            point._host = host;
            point._visual = host.transform;
            point.SnapToHologram();
            return point;
        }

        void OnEnable()
        {
            Collider ours = GetComponent<Collider>();
            if (ours == null)
                return;
            CharacterController[] bodies = FindObjectsByType<CharacterController>(FindObjectsSortMode.None);
            for (int i = 0; i < bodies.Length; i++)
                Physics.IgnoreCollision(ours, bodies[i], true);
        }

        public static void SuppressPrompt()
        {
            InteractableHoverTracker tracker = GameServices.Get<InteractableHoverTracker>();
            if (tracker != null && tracker.CurrentHoveredInteractable != null)
                tracker.Clear();
            foreach (InteractionCanvas tip in Prompts())
                tip.gameObject.SetActive(false);
        }

        // InteractionTooltipsActivator creates the prompt canvas under itself on every hover.
        static InteractionCanvas[] Prompts()
        {
            InteractionTooltipsActivator tips = GameServices.Find<InteractionTooltipsActivator>();
            return tips == null ? new InteractionCanvas[0] : tips.GetComponentsInChildren<InteractionCanvas>();
        }

        public bool Interact(InteractionType type)
        {
            if (type != InteractionType.Interact || _host == null || !_host.HasWork)
                return false;
            try
            {
                BlueprintSupplyMenu.Open(_host);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
            }
            return true;
        }

        public void EnableHover()
        {
        }

        public void DisableHover()
        {
        }

        void LateUpdate()
        {
            SnapToHologram();
            InteractableHoverTracker tracker = GameServices.Get<InteractableHoverTracker>();
            if (tracker == null)
                return;
            bool ours = ReferenceEquals(tracker.CurrentHoveredInteractable, this);
            if (BlueprintSupplyMenu.Current != null || SchematicScreen.IsOpen)
            {
                if (ours)
                    tracker.Clear();
                return;
            }
            // The game's own hover wins: replacing it without a hover-leave would leave its prompt on the screen.
            if (LookingAtHologram())
            {
                if (tracker.CurrentHoveredInteractable == null)
                {
                    tracker.Set(this);
                    ours = true;
                }
            }
            else if (ours)
            {
                tracker.Clear();
                ours = false;
            }
            if (ours)
                LabelPrompt();
        }

        // The game labels the prompt with the borrowed part interaction; the blueprint says "Open". Each hover creates
        // a new canvas, so only a new one is relabelled.
        void LabelPrompt()
        {
            foreach (InteractionCanvas tip in Prompts())
            {
                int id = tip.GetInstanceID();
                if (id == _labelledCanvas)
                    continue;
                _labelledCanvas = id;
                foreach (TMP_Text label in tip.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (label.GetComponentInParent<KeyTipIcon>(true) != null)
                        continue;
                    LocalizeStringEvent localize = label.GetComponent<LocalizeStringEvent>();
                    if (localize != null)
                        localize.enabled = false;
                    label.text = "Open";
                }
            }
        }

        // The box follows the hologram in the visual scene, shifted into the physics scene where the interaction ray
        // runs.
        public void SnapToHologram()
        {
            if (_visual == null)
                return;
            if (ClientScene.Scene.IsValid() && gameObject.scene != ClientScene.Scene)
                SceneManager.MoveGameObjectToScene(gameObject, ClientScene.Scene);

            IWorldTransposeClient transpose = GameServices.Get<IWorldTransposeClient>();
            PlayersClientTracker players = GameServices.Get<PlayersClientTracker>();
            Vector3 position = _visual.position;
            if (transpose != null && players != null && players.LocalPlayer != null)
            {
                var body = players.LocalPlayer;
                Vector3 physicsBody = transpose.Position(body.PhysicsState.SpaceId, ClientOrigin.LocalPhysicsSpaceId, body.PhysicsState.Position);
                position += physicsBody - body.VisualPos;
            }
            transform.SetPositionAndRotation(position, _visual.rotation);
        }

        bool LookingAtHologram()
        {
            Camera camera = Camera.main;
            if (camera == null || _visual == null || _host == null)
                return false;
            Bounds bounds = _host.LocalBounds;
            Vector3 origin = _visual.InverseTransformPoint(camera.transform.position);
            float reach = bounds.extents.magnitude + DetectionDistance;
            if ((origin - bounds.center).sqrMagnitude > reach * reach)
                return false;
            if (bounds.Contains(origin))
                return true;
            var ray = new Ray(origin, _visual.InverseTransformDirection(camera.transform.forward));
            return bounds.IntersectRay(ray, out float distance) && distance <= DetectionDistance;
        }

        static void EnsureLabel(ShipPartVisuals hologram)
        {
            if (_interactions != null)
                return;

            PartInteractionConfig config = default;
            bool found = hologram != null && TakeLabel(hologram.GetComponentsInChildren<PartInteraction>(true), ref config);
            if (!found)
                found = TakeLabel(FindObjectsByType<PartInteraction>(FindObjectsInactive.Include, FindObjectsSortMode.None), ref config);
            if (!found)
            {
                config = new PartInteractionConfig
                {
                    type = InteractionType.Interact,
                    localizedLabel = new LocalizedString()
                };
            }
            _interactions = new List<PartInteractionConfig> { config };
        }

        static bool TakeLabel(PartInteraction[] parts, ref PartInteractionConfig config)
        {
            if (parts == null)
                return false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null || parts[i].Interactions == null)
                    continue;
                foreach (PartInteractionConfig candidate in parts[i].Interactions)
                {
                    if (candidate.type == InteractionType.Interact && candidate.localizedLabel != null)
                    {
                        config = candidate;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
