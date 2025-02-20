﻿// Copyright (c) CypherCore <http://github.com/CypherCore> All rights reserved.
// Licensed under the GNU GENERAL PUBLIC LICENSE. See LICENSE file in the project root for full license information.

using Framework.Constants;
using Framework.Dynamic;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game.Collision
{
    public enum VMAPLoadResult
    {
        Error,
        OK,
        Ignored
    }

    public enum LoadResult
    {
        Success,
        FileNotFound,
        VersionMismatch,
        ReadFromFileFailed,
        DisabledInConfig
    }

    public class VMapManager : Singleton<VMapManager>
    {
        VMapManager() { }

        public static string VMapPath = Global.WorldMgr.GetDataPath() + "/vmaps/";

        public void Initialize(MultiMap<uint, uint> mapData)
        {
            foreach (var pair in mapData)
                iParentMapData[pair.Value] = pair.Key;
        }

        public LoadResult LoadMap(uint mapId, int x, int y)
        {
            if (!IsMapLoadingEnabled())
                return LoadResult.DisabledInConfig;

            var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
            if (instanceTree == null)
            {
                string filename = VMapPath + GetMapFileName(mapId);
                StaticMapTree newTree = new(mapId);
                LoadResult treeInitResult = newTree.InitMap(filename);
                if (treeInitResult != LoadResult.Success)
                    return treeInitResult;

                iInstanceMapTrees.Add(mapId, newTree);

                instanceTree = newTree;
            }

            return instanceTree.LoadMapTile(x, y, this);
        }

        public void UnloadMap(uint mapId, int x, int y)
        {
            var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
            if (instanceTree != null)
            {
                instanceTree.UnloadMapTile(x, y, this);
                if (instanceTree.NumLoadedTiles() == 0)
                {
                    iInstanceMapTrees.Remove(mapId);
                }
            }
        }

        public void UnloadMap(uint mapId)
        {
            var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
            if (instanceTree != null)
            {
                instanceTree.UnloadMap();
                if (instanceTree.NumLoadedTiles() == 0)
                {
                    iInstanceMapTrees.Remove(mapId);
                }
            }
        }

        public bool IsInLineOfSight(uint mapId, float x1, float y1, float z1, float x2, float y2, float z2, ModelIgnoreFlags ignoreFlags)
        {
            if (!IsLineOfSightCalcEnabled() || Global.DisableMgr.IsVMAPDisabledFor(mapId, DisableFlags.VmapLOS))
                return true;

            var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
            if (instanceTree != null)
            {
                Vector3 pos1 = ConvertPositionToInternalRep(x1, y1, z1);
                Vector3 pos2 = ConvertPositionToInternalRep(x2, y2, z2);
                if (pos1 != pos2)
                    return instanceTree.IsInLineOfSight(pos1, pos2, ignoreFlags);
            }

            return true;
        }

        public bool GetObjectHitPos(uint mapId, float x1, float y1, float z1, float x2, float y2, float z2, out float rx, out float ry, out float rz, float modifyDist)
        {
            if (IsLineOfSightCalcEnabled() && !Global.DisableMgr.IsVMAPDisabledFor(mapId, DisableFlags.VmapLOS))
            {
                var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
                if (instanceTree != null)
                {
                    Vector3 resultPos;
                    Vector3 pos1 = ConvertPositionToInternalRep(x1, y1, z1);
                    Vector3 pos2 = ConvertPositionToInternalRep(x2, y2, z2);
                    bool result = instanceTree.GetObjectHitPos(pos1, pos2, out resultPos, modifyDist);
                    resultPos = ConvertPositionToInternalRep(resultPos.X, resultPos.Y, resultPos.Z);
                    rx = resultPos.X;
                    ry = resultPos.Y;
                    rz = resultPos.Z;
                    return result;
                }
            }

            rx = x2;
            ry = y2;
            rz = z2;

            return false;
        }

        public float GetHeight(uint mapId, float x, float y, float z, float maxSearchDist)
        {
            if (IsHeightCalcEnabled() && !Global.DisableMgr.IsVMAPDisabledFor(mapId, DisableFlags.VmapHeight))
            {
                var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
                if (instanceTree != null)
                {
                    Vector3 pos = ConvertPositionToInternalRep(x, y, z);
                    float height = instanceTree.GetHeight(pos, maxSearchDist);
                    if (float.IsInfinity(height))
                        height = MapConst.VMAPInvalidHeightValue; // No height

                    return height;
                }
            }

            return MapConst.VMAPInvalidHeightValue;
        }



        public bool GetAreaAndLiquidData(uint mapId, float x, float y, float z, byte? reqLiquidType, out AreaAndLiquidData data)
        {
            data = new AreaAndLiquidData();

            var instanceTree = iInstanceMapTrees.LookupByKey(mapId);
            if (instanceTree != null)
            {
                LocationInfo info = new();
                Vector3 pos = ConvertPositionToInternalRep(x, y, z);
                if (instanceTree.GetLocationInfo(pos, info))
                {
                    data.floorZ = info.ground_Z;

                    if (!Global.DisableMgr.IsVMAPDisabledFor(mapId, DisableFlags.VmapLiquidStatus))
                    {
                        uint liquidType = info.hitModel.GetLiquidType(); // entry from LiquidType.dbc
                        float liquidLevel = 0;
                        if (!reqLiquidType.HasValue || (Global.DB2Mgr.GetLiquidFlags(liquidType) & reqLiquidType.Value) != 0)
                            if (info.hitInstance.GetLiquidLevel(pos, info, ref liquidLevel))
                                data.liquidInfo = new(liquidType, liquidLevel);
                    }

                    if (!Global.DisableMgr.IsVMAPDisabledFor(mapId, DisableFlags.VmapLiquidStatus))
                        data.areaInfo = new((int)info.hitModel.GetWmoID(), info.hitInstance.adtId, info.rootId, info.hitModel.GetMogpFlags(), info.hitInstance.Id);

                    return true;
                }
            }

            return false;
        }

        public WorldModel AcquireModelInstance(string filename)
        {
            ManagedModel worldmodel; // this is intentionally declared before lock so that it is destroyed after it to prevent deadlocks in releaseModelInstance

            lock (LoadedModelFilesLock)
            {
                filename = filename.TrimEnd('\0');
                if (iLoadedModelFiles.TryGetValue(filename, out worldmodel))
                    return worldmodel.Model;

                worldmodel = new ManagedModel(filename);
                if (!worldmodel.Model.ReadFile(VMapPath + filename))
                {
                    Log.outError(LogFilter.Server, $"VMapManager: could not load '{filename}'");
                    return null;
                }

                Log.outDebug(LogFilter.Maps, $"VMapManager: loading file '{filename}'");

                iLoadedModelFiles.Add(filename, worldmodel);
                return worldmodel.Model;
            }
        }

        public void ReleaseModelInstance(string filename)
        {
            lock (LoadedModelFilesLock)
            {
                filename = filename.TrimEnd('\0');
                
                Log.outDebug(LogFilter.Maps, $"VMapManager: unloading file '{filename}'");

                var erased = iLoadedModelFiles.Remove(filename);
                if (!erased)
                {
                    Log.outError(LogFilter.Server, $"VMapManager: trying to unload non-loaded file '{filename}'");
                    return;
                }
            }
        }

        public LoadResult ExistsMap(uint mapId, int x, int y)
        {
            return StaticMapTree.CanLoadMap(VMapPath, mapId, x, y, this);
        }

        public int GetParentMapId(uint mapId)
        {
            if (iParentMapData.ContainsKey(mapId))
                return (int)iParentMapData[mapId];

            return -1;
        }

        Vector3 ConvertPositionToInternalRep(float x, float y, float z)
        {
            Vector3 pos = new();
            float mid = 0.5f * 64.0f * 533.33333333f;
            pos.X = mid - x;
            pos.Y = mid - y;
            pos.Z = z;

            return pos;
        }

        public static string GetMapFileName(uint mapId)
        {
            return $"{mapId:D4}/{mapId:D4}.vmtree";
        }

        public void SetEnableLineOfSightCalc(bool pVal) { _enableLineOfSightCalc = pVal; }
        public void SetEnableHeightCalc(bool pVal) { _enableHeightCalc = pVal; }

        public bool IsLineOfSightCalcEnabled() { return _enableLineOfSightCalc; }
        public bool IsHeightCalcEnabled() { return _enableHeightCalc; }
        public bool IsMapLoadingEnabled() { return _enableLineOfSightCalc || _enableHeightCalc; }

        Dictionary<string, ManagedModel> iLoadedModelFiles = new();
        Dictionary<uint, StaticMapTree> iInstanceMapTrees = new();
        Dictionary<uint, uint> iParentMapData = new();
        bool _enableLineOfSightCalc;
        bool _enableHeightCalc;

        object LoadedModelFilesLock = new();
    }

    public class ManagedModel
    {
        public WorldModel Model = new();
        string _name; // valid only while model is held in VMapManager2::iLoadedModelFiles

        public ManagedModel(string name)
        {
            _name = name;
        }

        ~ManagedModel()
        {
            Global.VMapMgr.ReleaseModelInstance(_name);
        }
    }

    public class AreaAndLiquidData
    {
        public class AreaInfo
        {
            public int GroupId;
            public int AdtId;
            public int RootId;
            public uint MogpFlags;
            public uint UniqueId;

            public AreaInfo(int groupId, int adtId, int rootId, uint mogpFlags, uint uniqueId)
            {
                GroupId = groupId;
                AdtId = adtId;
                RootId = rootId;
                MogpFlags = mogpFlags;
                UniqueId = uniqueId;
            }
        }

        public class LiquidInfo
        {
            public uint LiquidType;
            public float Level;

            public LiquidInfo(uint type, float level)
            {
                LiquidType = type;
                Level = level;
            }
        }

        public float floorZ = MapConst.VMAPInvalidHeightValue;
        public AreaInfo areaInfo;
        public LiquidInfo liquidInfo;
    }
}
