using System;
using Core.Utils;
using HarmonyLib;
using MelonLoader;
using Tools.Common.Hover;
using Tools.Recycler;
using UnityEngine;
using WorldTracking.Interface.Values;

namespace ShipSchematics
{
    // What the recycler holds while it aims at a blueprint. Recycling it scraps the blueprint.
    public sealed class BlueprintRecycleTarget : IHoveredObject
    {
        readonly SchematicBuildSite _host;

        public BlueprintRecycleTarget(SchematicBuildSite host)
        {
            _host = host;
        }

        public void EnableRecycleHighlight()
        {
            if (_host != null)
                _host.SetRecycleLook(true);
        }

        public void ClearHighlight()
        {
            if (_host != null)
                _host.SetRecycleLook(false);
        }

        public void RemoveObject()
        {
            if (_host != null)
                _host.Scrap();
        }

        public bool Equals(IHoveredObject other)
        {
            return other is BlueprintRecycleTarget target && target._host == _host;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as IHoveredObject);
        }

        public override int GetHashCode()
        {
            return _host == null ? 0 : _host.GetHashCode();
        }
    }

    // The recycler's own ray skips the Interactable layer, so it never sees a blueprint. While it aims at one, this
    // takes the hover instead of the game's update; otherwise the game's update runs and clears the blueprint once.
    [HarmonyPatch(typeof(RecyclerTool), "UpdateHoveredObject")]
    static class BlueprintRecyclePatch
    {
        static AccessTools.FieldRef<RecyclerTool, IHoveredObject> _hovered;
        static AccessTools.FieldRef<RecyclerTool, float> _distance;
        static int _mask;
        static bool _failed;

        static bool Prepare()
        {
            try
            {
                _hovered = AccessTools.FieldRefAccess<RecyclerTool, IHoveredObject>("hoveredObject");
                _distance = AccessTools.FieldRefAccess<RecyclerTool, float>("rayCastDistance");
                return AccessTools.Method(typeof(RecyclerTool), "UpdateHoveredObject") != null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("The recycler has changed; blueprints cannot be recycled. " + ex.Message);
                return false;
            }
        }

        static bool Prefix(RecyclerTool __instance, CameraPhysicsData cameraPhysics)
        {
            if (_failed || SchematicBuildSite.Active.Count == 0)
                return true;
            try
            {
                BlueprintPartPoint point = HitBlueprint(cameraPhysics, _distance(__instance));
                BlueprintRecycleTarget target = point == null || point.Host == null ? null : point.RecycleTarget;
                if (target == null)
                    return true;
                IHoveredObject current = _hovered(__instance);
                if (!target.Equals(current))
                {
                    if (current != null)
                        current.ClearHighlight();
                    target.EnableRecycleHighlight();
                    _hovered(__instance) = target;
                }
                return false;
            }
            catch (Exception ex)
            {
                _failed = true;
                MelonLogger.Warning("Recycling blueprints is off until the game restarts. " + ex.Message);
                return true;
            }
        }

        static BlueprintPartPoint HitBlueprint(CameraPhysicsData cameraPhysics, float distance)
        {
            if (_mask == 0)
                _mask = LayerMask.GetMask("Default", "Interactable", "Ship", "WorldObject");
            if (!ClientScene.PhysicsScene.Raycast(cameraPhysics.CameraPos, cameraPhysics.Direction, out RaycastHit hit, distance > 0f ? distance : 8f, _mask, QueryTriggerInteraction.Ignore))
                return null;
            return hit.collider.GetComponent<BlueprintPartPoint>();
        }
    }
}
