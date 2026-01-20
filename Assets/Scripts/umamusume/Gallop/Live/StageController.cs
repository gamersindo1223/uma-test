using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace Gallop.Live
{
    [Serializable]
    public class StageObjectUnit
    {
        public string UnitName;
        public GameObject[] ChildObjects;
        public string[] _childObjectNames;
    }

    public class StageController : MonoBehaviour
    {
        public List<GameObject> _stageObjects;
        public StageObjectUnit[] _stageObjectUnits;
        public Dictionary<string, StageObjectUnit> StageObjectUnitMap = new Dictionary<string, StageObjectUnit>();
        public Dictionary<string, GameObject> StageObjectMap = new Dictionary<string, GameObject>();
        public Dictionary<string, Transform> StageParentMap = new Dictionary<string, Transform>();

        // Track character props (attached to character bones) for timeline visibility control
        // Key is propsName (e.g., "003", "1024"), value is list of all prop instances with that name
        public Dictionary<string, List<GameObject>> _characterProps = new Dictionary<string, List<GameObject>>();
        // Also track by majorId for props that share propsName but have different visibility
        // Key is majorId (e.g., 1024 for uchiwa, 1355/1400 for drumsticks)
        public Dictionary<int, List<GameObject>> _characterPropsByMajorId = new Dictionary<int, List<GameObject>>();

        // ========== TIMELINE-FOCUSED PROP CONTROL ==========
        // Maps timeline object names to character prop majorIds for unified control
        // This allows timeline objects to directly control character props without hardcoding
        public class TimelinePropMapping
        {
            public string TimelineObjectName;    // e.g., "pfb_env_live10129_uchiwa000"
            public int CharacterPropMajorId;     // e.g., 1024
            public string CharacterPropsName;    // e.g., "1024"
            public bool InvertVisibility;        // If true, timeline True = prop Hidden
            public string Description;           // Human-readable description

            public override string ToString() =>
                $"[{TimelineObjectName}] -> majorId:{CharacterPropMajorId} (invert:{InvertVisibility}) - {Description}";
        }

        // Automatically built mapping table
        public List<TimelinePropMapping> TimelinePropMappings = new List<TimelinePropMapping>();

        // ========== ASSET LOAD TRACKING ==========
        // Track all asset load attempts for debugging
        public class AssetLoadAttempt
        {
            public string AssetPath;         // Full asset path
            public string AssetType;         // "CharacterProp", "StageProp", "TimelineProp"
            public bool Loaded;              // Whether it was successfully loaded
            public string FailReason;        // Reason for failure if not loaded
            public int MajorId;              // Major ID if applicable
            public string PropsName;         // propsName if applicable
        }
        public List<AssetLoadAttempt> AssetLoadAttempts = new List<AssetLoadAttempt>();

        public void TrackAssetLoad(string path, string type, bool loaded, string failReason = "", int majorId = 0, string propsName = "")
        {
            AssetLoadAttempts.Add(new AssetLoadAttempt
            {
                AssetPath = path,
                AssetType = type,
                Loaded = loaded,
                FailReason = failReason,
                MajorId = majorId,
                PropsName = propsName
            });
        }

        // Cache of timeline object visibility states for debugging
        public Dictionary<string, bool> CurrentTimelineObjectStates = new Dictionary<string, bool>();

        private void Awake()
        {
            InitializeStage();
            if (Director.instance)
            {
                Director.instance._stageController = this;
                Director.instance._liveTimelineControl.OnUpdateTransform += UpdateTransform;
                Director.instance._liveTimelineControl.OnUpdateObject += UpdateObject;

                // NOTE: LoadCharacterProps() and AttachStagePropsToCharacters() are called
                // from Director.InitializeTimeline() AFTER character locators are set up.
                // Do NOT call them here - locators don't exist yet!

                // Auto-attach prop debug exporter (press Q to export props)
                if (gameObject.GetComponent<PropDebugExporter>() == null)
                {
                    gameObject.AddComponent<PropDebugExporter>();
                    Debug.Log("[StageController] PropDebugExporter attached. Press 'Q' to export scene props.");
                }
            }
        }

        /// <summary>
        /// Find stage-loaded props (like microphones) that should be attached to character hands
        /// and move them from stage origin to character bones.
        /// Call this AFTER characters are loaded.
        /// </summary>
        public void AttachStagePropsToCharacters()
        {
            var locators = Director.instance?._liveTimelineControl?.liveCharactorLocators;
            if (locators == null || locators.Length == 0) return;

            // Determine how many characters should get props based on timeline data
            int maxPropsCharacters = GetMaxPropsCharacterCount();

            // Find any chr_prop objects loaded as stage objects (at origin instead of attached to hands)
            var propsToAttach = new List<GameObject>();
            foreach (var kvp in StageObjectMap)
            {
                if (kvp.Key.ToLower().Contains("chr_prop") && kvp.Value != null)
                {
                    // Check if it's at origin (not attached to anything meaningful)
                    if (kvp.Value.transform.parent != null &&
                        kvp.Value.transform.localPosition == Vector3.zero)
                    {
                        propsToAttach.Add(kvp.Value);
                    }
                }
            }

            if (propsToAttach.Count == 0) return;

            // Attach props to character hands
            // Default to right hand (Hand_Attach_R), which is typical for microphones
            string[] attachJoints = { "Hand_Attach_R", "Hand_Attach_L" };

            foreach (var prop in propsToAttach)
            {
                // Skip main performers (0 to maxPropsCharacters-1), only attach to backup dancers
                // Main performers should get their props from the propsDataGroup (microphones etc)
                int startIndex = maxPropsCharacters; // Start AFTER main performers
                for (int i = startIndex; i < locators.Length; i++)
                {
                    var locator = locators[i] as Gallop.Live.Cutt.LiveTimelineCharaLocator;
                    if (locator?.Bones == null) continue;

                    foreach (var jointName in attachJoints)
                    {
                        if (locator.Bones.TryGetValue(jointName, out Transform attachBone))
                        {
                            // Clone the prop for each character (original stays for first)
                            GameObject propToAttach;
                            if (i == 0)
                            {
                                propToAttach = prop;
                            }
                            else
                            {
                                propToAttach = Instantiate(prop);
                                propToAttach.name = prop.name + $"_char{i}";
                            }

                            propToAttach.transform.SetParent(attachBone);
                            propToAttach.transform.localPosition = Vector3.zero;
                            propToAttach.transform.localRotation = Quaternion.identity;
                            propToAttach.transform.localScale = Vector3.one;

                            Debug.Log($"[StageController] Attached '{propToAttach.name}' to character {i} bone '{jointName}'");
                            break; // Only attach to first valid joint per character
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determine how many characters should receive props based on the timeline propsDataGroup.
        /// If propsConditionGroup specifies CharaPosition conditions, use those.
        /// Otherwise default to 4 (typical main performer count).
        /// </summary>
        private int GetMaxPropsCharacterCount()
        {
            var data = Director.instance?._liveTimelineControl?.data;
            if (data?.propsSettings?.propsDataGroup == null) return 4;

            int maxPosition = -1;
            foreach (var propGroup in data.propsSettings.propsDataGroup)
            {
                if (propGroup.propsConditionGroup == null) continue;

                foreach (var condGroup in propGroup.propsConditionGroup)
                {
                    if (condGroup.propsConditionData == null) continue;

                    foreach (var cond in condGroup.propsConditionData)
                    {
                        // CharaPosition type = 1, Value is the character index
                        if (cond.Type == LiveTimelinePropsSettings.PropsConditionType.CharaPosition)
                        {
                            maxPosition = Math.Max(maxPosition, cond.Value);
                        }
                    }
                }
            }

            // If we found character position conditions, use max+1 as count
            // Otherwise default to 4 (typical main performers)
            return maxPosition >= 0 ? maxPosition + 1 : 4;
        }

        private void OnDestroy()
        {
            if (Director.instance)
            {
                Director.instance._liveTimelineControl.OnUpdateTransform -= UpdateTransform;
                Director.instance._liveTimelineControl.OnUpdateObject -= UpdateObject;
            }
        }

        public void InitializeStage()
        {
            // Debug.Log($"[StageController] InitializeStage: {_stageObjects?.Count ?? 0} stage parts to process");

            foreach (GameObject stage_part in _stageObjects)
            {
                var instance = Instantiate(stage_part, transform);

                // Apply proper shaders to stage objects (fixes drums and other props not rendering)
                ApplyShadersToObject(instance);

                int childCount = 0;
                int lightCount = 0;
                foreach (var child in instance.GetComponentsInChildren<Transform>(true))
                {
                    if (!StageObjectMap.ContainsKey(child.name))
                    {
                        // Note: Lights are now visible for proper stage effects
                        // Apply shaders to light objects as well
                        if (child.name.Contains("light"))
                        {
                            ApplyShadersToObject(child.gameObject);
                            lightCount++;
                        }
                        var tmp_name = child.name.Replace("(Clone)", "");
                        StageObjectMap.Add(tmp_name, child.gameObject);
                        StageParentMap.TryAdd(tmp_name, child.gameObject.transform.parent);
                        childCount++;
                    }
                }
                // Debug.Log($"[StageController] Stage part '{stage_part?.name}': {childCount} objects added, {lightCount} lights");
            }

            foreach (var unit in _stageObjectUnits)
            {
                if (!StageObjectUnitMap.ContainsKey(unit.UnitName))
                {
                    StageObjectUnitMap.Add(unit.UnitName, unit);
                }
            }
        }

        /// <summary>
        /// Build timeline-to-prop mapping table by analyzing timeline objectList and propsSettings.
        /// This creates automatic links between stage objects and character props.
        /// Call this AFTER LoadCharacterProps() so prop dictionaries are populated.
        /// </summary>
        public void BuildTimelinePropMapping()
        {
            TimelinePropMappings.Clear();

            if (Director.instance?._liveTimelineControl?.data == null) return;
            var data = Director.instance._liveTimelineControl.data;

            Debug.Log("[StageController] Building timeline-to-prop mapping table...");

            // 1. Parse timeline objectList to find stage objects that control props
            if (data.worksheetList != null)
            {
                foreach (var worksheet in data.worksheetList)
                {
                    if (worksheet?.objectList == null) continue;
                    foreach (var objData in worksheet.objectList)
                    {
                        string objName = objData?.name;
                        if (string.IsNullOrEmpty(objName)) continue;

                        // Detect prop-controlling objects by name patterns
                        TimelinePropMapping mapping = null;

                        // Vocal speaker controls both:
                        // 1. propsName "003" (mic on Mic_Attach_00) - follow directly
                        // 2. majorId 1024 (hand mic on Hand_Attach_R) - INVERTED (when at stand, hide hand mic)
                        if (objName.Contains("vocal_speaker"))
                        {
                            // First add mapping for propsName "003" (mic stand)
                            TimelinePropMappings.Add(new TimelinePropMapping
                            {
                                TimelineObjectName = objName,
                                CharacterPropMajorId = 0,
                                CharacterPropsName = "003",
                                InvertVisibility = false,
                                Description = "Vocal speaker controls mic (003)"
                            });
                            
                            // Then add mapping for majorId 1024 (hand mic) - INVERTED
                            // When vocal_speaker=True (at stand), hand mic should be hidden
                            // When vocal_speaker=False (away from stand), hand mic should be visible
                            mapping = new TimelinePropMapping
                            {
                                TimelineObjectName = objName,
                                CharacterPropMajorId = 1024,
                                CharacterPropsName = "1024",
                                InvertVisibility = true, // Hand mic hidden when at mic stand
                                Description = "Vocal speaker controls hand mic (1024) - inverted"
                            };
                        }
                        // Drum (prop1401) -> Drumsticks (majorId 1355, 1400)
                        else if (objName.Contains("prop1401"))
                        {
                            // Add mapping for drumstick 1355
                            TimelinePropMappings.Add(new TimelinePropMapping
                            {
                                TimelineObjectName = objName,
                                CharacterPropMajorId = 1355,
                                CharacterPropsName = "1024",
                                InvertVisibility = false,
                                Description = "Taiko drum controls drumstick R"
                            });
                            // Add mapping for drumstick 1400
                            mapping = new TimelinePropMapping
                            {
                                TimelineObjectName = objName,
                                CharacterPropMajorId = 1400,
                                CharacterPropsName = "1024",
                                InvertVisibility = false,
                                Description = "Taiko drum controls drumstick L"
                            };
                        }

                        if (mapping != null)
                        {
                            TimelinePropMappings.Add(mapping);
                            Debug.Log($"[StageController] Mapping: {mapping}");
                        }
                    }
                }
            }

            Debug.Log($"[StageController] Built {TimelinePropMappings.Count} timeline-prop mappings");
        }

        /// <summary>
        /// Load stage props (like drums) directly from timeline objectList.
        /// Parses timeline for objects that look like props and loads them.
        /// </summary>
        public void LoadPropsFromTimeline()
        {
            if (Director.instance?._liveTimelineControl?.data == null) return;
            var data = Director.instance._liveTimelineControl.data;

            Debug.Log("[StageController] Loading props from timeline objectList...");

            if (data.worksheetList == null) return;

            foreach (var worksheet in data.worksheetList)
            {
                if (worksheet?.objectList == null) continue;
                foreach (var objData in worksheet.objectList)
                {
                    string objName = objData?.name;
                    if (string.IsNullOrEmpty(objName)) continue;

                    // Look for prop objects that should be loaded
                    // Pattern: pfb_chr_prop####_##_Variant or pfb_chr_prop####_##
                    if (objName.StartsWith("pfb_chr_prop"))
                    {
                        // Parse the majorId from the name (e.g., "pfb_chr_prop1401_00" -> 1401)
                        var parts = objName.Replace("pfb_chr_prop", "").Split('_');
                        if (parts.Length >= 1 && int.TryParse(parts[0], out int majorId))
                        {
                            int minorId = 0;
                            if (parts.Length >= 2)
                            {
                                // Remove any suffix like "Variant"
                                var minorPart = parts[1].Replace("Variant", "").Trim();
                                int.TryParse(minorPart, out minorId);
                            }

                            Debug.Log($"[StageController] Timeline references prop: majorId={majorId}, minorId={minorId}");

                            // Try to load this prop as a stage object
                            LoadStagePropFromTimeline(objName, majorId, minorId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Load a specific stage prop from timeline reference.
        /// </summary>
        private void LoadStagePropFromTimeline(string timelineObjName, int majorId, int minorId)
        {
            // Build asset path
            string propPath = $"3d/chara/prop/prop{majorId:D4}_{minorId:D2}/pfb_chr_prop{majorId:D4}_{minorId:D2}";
            string searchKey = $"pfb_chr_prop{majorId:D4}_{minorId:D2}";

            var abList = UmaViewerMain.Instance?.AbList;
            if (abList == null)
            {
                Debug.LogWarning("[StageController] AbList not available");
                return;
            }

            // Find the prop entry - FirstOrDefault returns default struct if not found
            var propEntry = abList.FirstOrDefault(a => a.Key.Contains(searchKey) && a.Key.Contains("pfb_chr_prop"));

            // Check if we found a valid entry (Key will be null if default struct)
            if (string.IsNullOrEmpty(propEntry.Key) || propEntry.Value == null)
            {
                Debug.LogWarning($"[StageController] Timeline prop not found: {searchKey}");
                return;
            }

            Debug.Log($"[StageController] Loading timeline prop: {propEntry.Key}");

            var propPrefab = propEntry.Value.Get<GameObject>();
            if (propPrefab == null)
            {
                Debug.LogWarning($"[StageController] Failed to load timeline prop prefab: {propPath}");
                return;
            }

            // Instantiate as a stage object
            var propInstance = Instantiate(propPrefab, transform);
            propInstance.name = timelineObjName;

            // Apply shaders
            ApplyShadersToObject(propInstance);

            // Register in StageObjectMap for timeline control
            string registrationName = timelineObjName;
            if (!StageObjectMap.ContainsKey(registrationName))
            {
                StageObjectMap.Add(registrationName, propInstance);
                StageParentMap.TryAdd(registrationName, propInstance.transform.parent);
                Debug.Log($"[StageController] Registered timeline prop '{registrationName}' in StageObjectMap");
            }

            // Start inactive - timeline will control visibility
            propInstance.SetActive(false);
        }

        /// <summary>
        /// Load character props (microphones, instruments) from propsSettings and attach to characters
        /// </summary>
        public void LoadCharacterProps()
        {
            if (Director.instance == null || Director.instance._liveTimelineControl == null)
            {
                Debug.LogWarning("[StageController] Cannot load character props - Director not ready");
                return;
            }

            var data = Director.instance._liveTimelineControl.data;
            if (data == null)
            {
                Debug.LogWarning("[StageController] Cannot load character props - data is null");
                return;
            }

            Debug.Log($"[StageController] LoadCharacterProps: data exists, propsSettings={(data.propsSettings != null ? "present" : "NULL")}");

            if (data.propsSettings == null)
            {
                Debug.LogWarning("[StageController] propsSettings is null");
                return;
            }

            if (data.propsSettings.propsDataGroup == null)
            {
                Debug.Log("[StageController] propsDataGroup is null");
                return;
            }

            Debug.Log($"[StageController] LoadCharacterProps: {data.propsSettings.propsDataGroup.Length} prop groups, count={data.propsSettings.propsDataGroupCount}");

            // Also log existing stage object props and their positions
            foreach (var kvp in StageObjectMap)
            {
                if (kvp.Key.ToLower().Contains("prop") && kvp.Value != null)
                {
                    Debug.Log($"[StageController] STAGE PROP found: '{kvp.Key}' at worldPos={kvp.Value.transform.position}, active={kvp.Value.activeSelf}");
                }
            }

            foreach (var propGroup in data.propsSettings.propsDataGroup)
            {
                if (propGroup == null) continue;

                Debug.Log($"[StageController] PropGroup: {propGroup.propsName}, isCharaProps={propGroup.isCharaProps}, majorId={propGroup.charaPropsMajorId}, minorId={propGroup.charaPropsMinorId}");

                // Load props that have attach joints (either character props OR props with attachJointNames like mics)
                bool hasAttachJoints = propGroup.attachJointNames != null && propGroup.attachJointNames.Length > 0;
                
                if (propGroup.isCharaProps && hasAttachJoints)
                {
                    // Character props: Build prop asset path based on majorId and minorId
                    // CORRECT Format: 3d/chara/prop/prop1024_00/pfb_chr_prop1024_00 (no underscore before number)
                    string propPath = $"3d/chara/prop/prop{propGroup.charaPropsMajorId:D4}_{propGroup.charaPropsMinorId:D2}/pfb_chr_prop{propGroup.charaPropsMajorId:D4}_{propGroup.charaPropsMinorId:D2}";

                    Debug.Log($"[StageController] Loading character prop: {propPath}");
                    LoadPropForCharacters(propPath, propGroup.attachJointNames, propGroup.propsName, propGroup.charaPropsMajorId, propGroup.propsConditionGroup);
                }
                else if (!propGroup.isCharaProps && hasAttachJoints && propGroup.charaPropsMajorId > 0)
                {
                    // Non-character props with attach joints AND a valid majorId
                    // Load using the majorId instead of assuming prop type from propsName/joint name
                    string propPath = $"3d/chara/prop/prop{propGroup.charaPropsMajorId:D4}_{propGroup.charaPropsMinorId:D2}/pfb_chr_prop{propGroup.charaPropsMajorId:D4}_{propGroup.charaPropsMinorId:D2}";
                    Debug.Log($"[StageController] Loading attachable stage prop (majorId={propGroup.charaPropsMajorId}): {propPath}");
                    LoadPropForCharacters(propPath, propGroup.attachJointNames, propGroup.propsName, propGroup.charaPropsMajorId, propGroup.propsConditionGroup);
                }
                // Note: propsName with majorId=0 and isCharaProps=false is a stage prop reference (e.g., fan stand)
                // These are not props to load - they already exist in the scene or reference stage objects
            }
        }

        /// <summary>
        /// Load stage props (non-character props like drums, signs) from propsSettings
        /// These are props that are placed on stage rather than attached to characters.
        /// They are registered in StageObjectMap for timeline control.
        /// </summary>
        public void LoadStageProps()
        {
            if (Director.instance == null || Director.instance._liveTimelineControl == null)
            {
                Debug.LogWarning("[StageController] Cannot load stage props - Director not ready");
                return;
            }

            var data = Director.instance._liveTimelineControl.data;
            if (data?.propsSettings?.propsDataGroup == null)
            {
                Debug.Log("[StageController] No stage props to load");
                return;
            }

            Debug.Log($"[StageController] LoadStageProps: Checking {data.propsSettings.propsDataGroup.Length} prop groups for stage props");

            foreach (var propGroup in data.propsSettings.propsDataGroup)
            {
                if (propGroup == null) continue;

                // Handle stage props (isCharaProps = false)
                if (!propGroup.isCharaProps)
                {
                    Debug.Log($"[StageController] Found STAGE prop: propsName='{propGroup.propsName}', majorId={propGroup.charaPropsMajorId}");

                    // Stage props with propsName reference an existing stage object by name
                    // The object should already be in StageObjectMap from InitializeStage
                    // We need to ensure it starts INACTIVE for timeline control
                    if (!string.IsNullOrEmpty(propGroup.propsName))
                    {
                        if (StageObjectMap.TryGetValue(propGroup.propsName, out GameObject stageObj))
                        {
                            // Start inactive - timeline will control visibility via UpdateObject
                            stageObj.SetActive(false);
                            Debug.Log($"[StageController] Stage prop '{propGroup.propsName}' set to INACTIVE for timeline control");
                        }
                        else
                        {
                            Debug.LogWarning($"[StageController] Stage prop '{propGroup.propsName}' NOT FOUND in StageObjectMap! Available: {string.Join(", ", StageObjectMap.Keys.Take(20))}...");
                        }
                    }

                    // If majorId > 0, this might reference a chr_prop asset to load
                    if (propGroup.charaPropsMajorId > 0)
                    {
                        LoadStagePropAsset(propGroup);
                    }
                }
            }
        }

        /// <summary>
        /// Load a stage prop asset from the database and add to scene
        /// </summary>
        private void LoadStagePropAsset(LiveTimelinePropsSettings.PropsDataGroup propGroup)
        {
            // Build search key for the prop asset
            string searchKey = $"pfb_chr_prop{propGroup.charaPropsMajorId:D4}_{propGroup.charaPropsMinorId:D2}";
            Debug.Log($"[StageController] Searching for stage prop asset: {searchKey}");

            var propEntry = UmaViewerMain.Instance.AbList
                .FirstOrDefault(a => a.Key.Contains(searchKey) && a.Key.Contains("pfb_chr_prop"));

            if (propEntry.Value == null)
            {
                Debug.LogWarning($"[StageController] Stage prop asset not found: {searchKey}");
                return;
            }

            var propPrefab = propEntry.Value.Get<GameObject>();
            if (propPrefab == null)
            {
                Debug.LogWarning($"[StageController] Failed to load stage prop prefab: {searchKey}");
                return;
            }

            // Instantiate the stage prop
            var propInstance = Instantiate(propPrefab, transform);
            propInstance.name = propGroup.propsName ?? $"stage_prop_{propGroup.charaPropsMajorId}";

            // Apply proper shaders
            ApplyShadersToObject(propInstance);

            // Register in StageObjectMap for timeline control
            if (!StageObjectMap.ContainsKey(propInstance.name))
            {
                StageObjectMap.Add(propInstance.name, propInstance);
                StageParentMap.TryAdd(propInstance.name, propInstance.transform);
            }

            // Start INACTIVE - timeline will control visibility
            propInstance.SetActive(false);

            Debug.Log($"[StageController] Loaded stage prop asset '{propInstance.name}' - set to INACTIVE for timeline control");
        }

        /// <summary>
        /// Prepare existing stage objects that are referenced by the timeline's objectDataList.
        /// These objects should start INACTIVE and be controlled by UpdateObject.
        /// </summary>
        public void PrepareTimelineObjects()
        {
            if (Director.instance == null || Director.instance._liveTimelineControl == null)
            {
                return;
            }

            var data = Director.instance._liveTimelineControl.data;
            if (data?.worksheetList == null) return;

            // Collect all object names that the timeline will control
            var timelineControlledObjects = new HashSet<string>();
            foreach (var worksheet in data.worksheetList)
            {
                if (worksheet?.objectList == null) continue;
                foreach (var objData in worksheet.objectList)
                {
                    if (!string.IsNullOrEmpty(objData?.name))
                    {
                        timelineControlledObjects.Add(objData.name);
                    }
                }
            }

            Debug.Log($"[StageController] Timeline controls {timelineControlledObjects.Count} objects");

            // Set ALL timeline-controlled stage objects to inactive initially
            // The timeline's UpdateObject will set correct visibility based on keyframe data
            int preparedCount = 0;
            var notFound = new System.Collections.Generic.List<string>();

            foreach (var objName in timelineControlledObjects)
            {
                GameObject foundObj = null;

                // Try exact match first
                if (StageObjectMap.TryGetValue(objName, out foundObj) && foundObj != null)
                {
                    foundObj.SetActive(false);
                    preparedCount++;
                    continue;
                }

                // Try with (Clone) suffix removed
                string cleanName = objName.Replace("(Clone)", "").Trim();
                if (StageObjectMap.TryGetValue(cleanName, out foundObj) && foundObj != null)
                {
                    foundObj.SetActive(false);
                    preparedCount++;
                    continue;
                }

                // Try without _set suffix (kantou_r_001_set -> kantou_r_001)
                if (cleanName.EndsWith("_set"))
                {
                    string noSetName = cleanName.Substring(0, cleanName.Length - 4);
                    if (StageObjectMap.TryGetValue(noSetName, out foundObj) && foundObj != null)
                    {
                        foundObj.SetActive(false);
                        preparedCount++;
                        continue;
                    }
                }

                // For pfb_ prefixed objects, try searching by partial match
                if (objName.StartsWith("pfb_"))
                {
                    // Some objects are stored with (Clone) in the map
                    foreach (var kvp in StageObjectMap)
                    {
                        if (kvp.Key.StartsWith(objName) || kvp.Key.Contains(objName))
                        {
                            kvp.Value.SetActive(false);
                            preparedCount++;
                            foundObj = kvp.Value;
                            break;
                        }
                    }
                }

                if (foundObj == null)
                {
                    notFound.Add(objName);
                }
            }

            if (notFound.Count > 0)
            {
                Debug.LogWarning($"[StageController] {notFound.Count} timeline objects NOT FOUND in StageObjectMap: {string.Join(", ", notFound)}");
            }

            Debug.Log($"[StageController] Prepared {preparedCount} objects for timeline control (set INACTIVE)");
        }

        private void LoadPropForCharacters(string propPath, string[] attachJointNames, string propName, int majorId, LiveTimelinePropsSettings.PropsConditionGroup[] conditionGroups)
        {
            var locators = Director.instance._liveTimelineControl.liveCharactorLocators;
            if (locators == null) return;

            // Search key: pfb_chr_prop1024_00 (the filename part)
            string searchKey = propPath.Split('/').Last();
            Debug.Log($"[StageController] Searching for prop with key: {searchKey}");

            var propEntry = UmaViewerMain.Instance.AbList
                .FirstOrDefault(a => a.Key.Contains(searchKey) && a.Key.Contains("pfb_chr_prop"));

            if (propEntry.Value == null)
            {
                Debug.LogWarning($"[StageController] Prop asset not found matching: {searchKey}");
                return;
            }

            Debug.Log($"[StageController] Found prop asset: {propEntry.Key}");

            var propPrefab = propEntry.Value.Get<GameObject>();
            if (propPrefab == null)
            {
                Debug.LogWarning($"[StageController] Failed to load prop prefab: {propPath}");
                return;
            }

            // Determine which character positions should receive this prop
            var targetPositions = new List<int>();
            if (conditionGroups != null)
            {
                foreach (var condGroup in conditionGroups)
                {
                    if (condGroup?.propsConditionData == null) continue;
                    foreach (var cond in condGroup.propsConditionData)
                    {
                        if (cond.Type == LiveTimelinePropsSettings.PropsConditionType.CharaPosition)
                        {
                            targetPositions.Add(cond.Value);
                        }
                    }
                }
            }

            // If no conditions, attach to all characters (fallback)
            if (targetPositions.Count == 0)
            {
                Debug.Log($"[StageController] No CharaPosition conditions for prop '{propName}', attaching to all characters");
                for (int i = 0; i < locators.Length; i++)
                {
                    targetPositions.Add(i);
                }
            }

            Debug.Log($"[StageController] Prop '{propName}' targets character positions: [{string.Join(", ", targetPositions)}]");

            // Attach only to characters matching the condition
            foreach (int charPos in targetPositions)
            {
                if (charPos < 0 || charPos >= locators.Length) continue;

                var locator = locators[charPos] as LiveTimelineCharaLocator;
                if (locator?.Bones == null) continue;

                foreach (var jointName in attachJointNames)
                {
                    if (locator.Bones.TryGetValue(jointName, out Transform attachBone))
                    {
                        var propInstance = Instantiate(propPrefab, attachBone);
                        propInstance.transform.localPosition = Vector3.zero;
                        propInstance.transform.localRotation = Quaternion.identity;
                        propInstance.name = $"{propName}_char{charPos}_{jointName}";

                        // Apply shaders
                        ApplyShadersToObject(propInstance);

                        // Register character prop for timeline visibility control
                        // The propsName (e.g., "003", "1024") links to objectList entries
                        if (!_characterProps.ContainsKey(propName))
                        {
                            _characterProps[propName] = new List<GameObject>();
                        }
                        _characterProps[propName].Add(propInstance);
                        
                        // Also register by majorId for props that share propsName
                        if (majorId > 0)
                        {
                            if (!_characterPropsByMajorId.ContainsKey(majorId))
                            {
                                _characterPropsByMajorId[majorId] = new List<GameObject>();
                            }
                            _characterPropsByMajorId[majorId].Add(propInstance);
                        }

                        // Start ACTIVE - props should be visible initially
                        // Timeline's UpdateObject will control visibility (hide when needed)
                        propInstance.SetActive(true);

                        Debug.Log($"[StageController] Attached prop '{propName}' (majorId={majorId}) to character {charPos} bone '{jointName}' - set ACTIVE initially");
                        break; // Only attach to first valid joint per character
                    }
                }
            }
        }

        /// <summary>
        /// Apply game shaders to stage objects, with fallback for unsupported shaders
        /// </summary>
        private void ApplyShadersToObject(GameObject obj)
        {
            var builder = UmaViewerBuilder.Instance;
            if (builder == null || builder.ShaderList == null)
            {
                Debug.LogWarning($"[StageController] Cannot apply shaders - builder or ShaderList is null");
                return;
            }

            int appliedCount = 0;
            int fallbackCount = 0;
            int skippedCount = 0;

            foreach (Renderer r in obj.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;

                    var shaderName = m.shader?.name ?? "(null)";
                    var gameShader = builder.ShaderList.Find(s => s != null && s.name == shaderName);

                    if (gameShader != null)
                    {
                        m.shader = gameShader;
                        appliedCount++;
                    }
                    else if (m.shader == null || !m.shader.isSupported)
                    {
                        // Try multiple fallbacks in order of preference
                        Shader fallback = null;
                        if (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null)
                        {
                            // Has texture - use Unlit/Texture
                            fallback = Shader.Find("Unlit/Texture");
                        }
                        else if (m.HasProperty("_Color"))
                        {
                            // Has color but no texture - use Unlit/Color
                            fallback = Shader.Find("Unlit/Color");
                        }

                        if (fallback == null)
                        {
                            fallback = Shader.Find("Standard");
                        }

                        if (fallback != null)
                        {
                            // Debug.Log($"[StageController] Fallback shader for '{r.name}' mat '{m.name}': {shaderName} -> {fallback.name}");
                            m.shader = fallback;

                            // Enable emission for light objects using Standard shader
                            if (fallback.name == "Standard" && r.name.ToLower().Contains("light"))
                            {
                                m.EnableKeyword("_EMISSION");
                                if (m.HasProperty("_EmissionColor"))
                                {
                                    var currentColor = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                                    m.SetColor("_EmissionColor", currentColor * 2f);
                                }
                            }

                            fallbackCount++;
                        }
                    }
                    else
                    {
                        // Shader is supported, keep it
                        skippedCount++;
                    }
                }
            }

            // Debug.Log($"[StageController] ApplyShadersToObject({obj.name}): applied={appliedCount}, fallback={fallbackCount}, kept={skippedCount}");
        }

        public void UpdateObject(ref ObjectUpdateInfo updateInfo) {

            if (updateInfo.data == null)
            {
                return;
            }

            // Try to find the object using various name formats
            string objName = updateInfo.data.name;
            GameObject gameObject = null;

            // Try exact match first
            if (!StageObjectMap.TryGetValue(objName, out gameObject) || gameObject == null)
            {
                // Try with (Clone) suffix removed
                string cleanName = objName.Replace("(Clone)", "").Trim();
                if (!StageObjectMap.TryGetValue(cleanName, out gameObject) || gameObject == null)
                {
                    // Try without _set suffix (kantou_r_001_set -> kantou_r_001)
                    if (cleanName.EndsWith("_set"))
                    {
                        string noSetName = cleanName.Substring(0, cleanName.Length - 4);
                        StageObjectMap.TryGetValue(noSetName, out gameObject);
                    }

                    // For pfb_ prefixed objects, try partial match
                    if (gameObject == null && objName.StartsWith("pfb_"))
                    {
                        foreach (var kvp in StageObjectMap)
                        {
                            if (kvp.Key.StartsWith(objName) || kvp.Key.Contains(objName))
                            {
                                gameObject = kvp.Value;
                                break;
                            }
                        }
                    }
                }
            }

            if (gameObject == null)
            {
                // Log when key timeline objects cannot be found
                if (objName.Contains("uchiwa") || objName.Contains("prop") || objName.Contains("003"))
                {
                    Debug.LogWarning($"[StageController] UpdateObject: CANNOT FIND '{objName}' in StageObjectMap!");
                }
                return;
            }

            // Check if this stage object needs visibility inversion
            // Some objects have inverted timeline logic (renderEnable=True means hidden)
            bool actualVisibility = updateInfo.renderEnable;
            bool invertVisibility = false;
            
            // Drum (prop1401) and Uchiwa (uchiwa) stage objects should NOT be inverted
            // DIRECT MAPPING: Timeline renderEnable=True means VISIBLE
            if (false) // Disabled inversion block
            {
                invertVisibility = true;
                actualVisibility = !updateInfo.renderEnable;
            }

            gameObject.SetActive(actualVisibility);
            
            // Debug log for key stage props to track visibility changes
            if (objName.Contains("uchiwa") || objName.Contains("prop") || objName.Contains("003"))
            {
                Debug.Log($"[StageController] UpdateObject: '{objName}' visibility = {actualVisibility} (inverted={invertVisibility}, raw={updateInfo.renderEnable}), AttachTarget = {updateInfo.AttachTarget}");
            }

            // Also update character props that match this object name or are associated with it
            // For uchiwa stage objects, also control the hand-held uchiwa props (propsName "1024")
            UpdateCharacterPropsVisibility(objName, updateInfo.renderEnable);

            Transform attach_transform = null;
            switch (updateInfo.AttachTarget)
            {
                case AttachType.None:
                    if(StageParentMap.TryGetValue(updateInfo.data.name, out Transform parentTransform))
                    {
                        attach_transform = parentTransform;
                    }
                    break;
                case AttachType.Character:
                    var chara = Director.instance.CharaContainerScript[updateInfo.CharacterPosition];
                    if (chara)
                    {
                        attach_transform = chara.transform;
                        
                        // For uchiwa (fan) stage objects, attach to hand bone instead of character root
                        // This ensures the fan follows hand movements during animations
                        if (objName.Contains("uchiwa"))
                        {
                            var locators = Director.instance?._liveTimelineControl?.liveCharactorLocators;
                            if (locators != null && updateInfo.CharacterPosition < locators.Length)
                            {
                                var locator = locators[updateInfo.CharacterPosition] as Gallop.Live.Cutt.LiveTimelineCharaLocator;
                                if (locator?.Bones != null && locator.Bones.TryGetValue("Hand_Attach_R", out Transform handBone))
                                {
                                    attach_transform = handBone;
                                    Debug.Log($"[StageController] Attaching uchiwa '{objName}' to Hand_Attach_R of character {updateInfo.CharacterPosition}");
                                }
                            }
                        }
                    }
                    break;
                case AttachType.Camera:
                    attach_transform = Director.instance.MainCameraTransform;
                    break;
            }
            if (gameObject.transform.parent != attach_transform)
            {
                gameObject.transform.SetParent(attach_transform);
            }

            if (updateInfo.data.enablePosition)
            {
                gameObject.transform.localPosition = updateInfo.updateData.position;
            }
            if (updateInfo.data.enableRotate)
            {
                gameObject.transform.localRotation = updateInfo.updateData.rotation;
            }
            if (updateInfo.data.enableScale)
            {
                gameObject.transform.localScale = updateInfo.updateData.scale;
            }
        }

        /// <summary>
        /// Update character prop visibility based on stage object name.
        /// Uses the TimelinePropMappings table for data-driven control.
        /// Falls back to hardcoded patterns if no mapping found.
        /// </summary>
        private void UpdateCharacterPropsVisibility(string stageObjectName, bool visible)
        {
            // Track current state for debug overlay
            CurrentTimelineObjectStates[stageObjectName] = visible;

            // 1. Try direct match: if objName matches a propsName directly
            if (_characterProps.TryGetValue(stageObjectName, out var directProps))
            {
                foreach (var prop in directProps)
                {
                    if (prop != null) prop.SetActive(visible);
                }
                Debug.Log($"[StageController] UpdateCharacterPropsVisibility: '{stageObjectName}' (direct) -> {visible}");
                return;
            }

            // 2. Use mapping table (data-driven approach)
            bool foundMapping = false;
            foreach (var mapping in TimelinePropMappings)
            {
                if (stageObjectName.Contains(mapping.TimelineObjectName) ||
                    mapping.TimelineObjectName.Contains(stageObjectName) ||
                    stageObjectName == mapping.TimelineObjectName)
                {
                    foundMapping = true;
                    bool actualVisibility = mapping.InvertVisibility ? !visible : visible;

                    // Apply to majorId if specified
                    if (mapping.CharacterPropMajorId > 0)
                    {
                        if (_characterPropsByMajorId.TryGetValue(mapping.CharacterPropMajorId, out var majorIdProps))
                        {
                            foreach (var prop in majorIdProps)
                            {
                                if (prop != null) prop.SetActive(actualVisibility);
                            }
                            Debug.Log($"[StageController] MAPPING: '{stageObjectName}' -> majorId:{mapping.CharacterPropMajorId} = {actualVisibility} (inv:{mapping.InvertVisibility})");
                        }
                    }
                    // Apply to propsName if majorId not specified
                    else if (!string.IsNullOrEmpty(mapping.CharacterPropsName))
                    {
                        if (_characterProps.TryGetValue(mapping.CharacterPropsName, out var propsNameProps))
                        {
                            foreach (var prop in propsNameProps)
                            {
                                if (prop != null) prop.SetActive(actualVisibility);
                            }
                            Debug.Log($"[StageController] MAPPING: '{stageObjectName}' -> propsName:{mapping.CharacterPropsName} = {actualVisibility} (inv:{mapping.InvertVisibility})");
                        }
                    }
                }
            }

            if (foundMapping) return;

            // 3. Fallback: Hardcoded patterns (for backwards compatibility)
            if (stageObjectName.Contains("vocal_speaker"))
            {
                // Control propsName "003" (mic stand) - follows visibility directly
                if (_characterProps.TryGetValue("003", out var micProps))
                {
                    foreach (var prop in micProps)
                    {
                        if (prop != null) prop.SetActive(visible);
                    }
                    Debug.Log($"[StageController] FALLBACK: '{stageObjectName}' -> Mic (003) visibility = {visible}");
                }
                
                // Control majorId 1024 (hand mic) - INVERTED visibility
                // When vocal_speaker visible (at stand), hand mic should be hidden
                if (_characterPropsByMajorId.TryGetValue(1024, out var handMicProps))
                {
                    bool invertedVisibility = !visible;
                    foreach (var prop in handMicProps)
                    {
                        if (prop != null) prop.SetActive(invertedVisibility);
                    }
                    Debug.Log($"[StageController] FALLBACK: '{stageObjectName}' -> Hand mic (1024) visibility = {invertedVisibility} (inverted)");
                }
            }
            else if (stageObjectName.Contains("prop1401") || stageObjectName.Contains("taiko"))
            {
                int[] drumstickIds = { 1355, 1400 };
                foreach (int stickId in drumstickIds)
                {
                    if (_characterPropsByMajorId.TryGetValue(stickId, out var stickProps))
                    {
                        foreach (var prop in stickProps)
                        {
                            if (prop != null) prop.SetActive(visible);
                        }
                    }
                }
                Debug.Log($"[StageController] FALLBACK: '{stageObjectName}' -> drumsticks visibility = {visible}");
            }
        }

        public void UpdateTransform(ref TransformUpdateInfo updateInfo)
        {
            if (updateInfo.data == null)
            {
                return;
            }
            if (StageObjectUnitMap.TryGetValue(updateInfo.data.name, out StageObjectUnit objectUnit))
            {
                foreach(var child in objectUnit.ChildObjects)
                {
                    if (StageObjectMap.TryGetValue(child.name, out GameObject gameObject))
                    {
                        if (updateInfo.data.enablePosition)
                        {
                            gameObject.transform.localPosition = updateInfo.updateData.position;
                        }
                        if (updateInfo.data.enableRotate)
                        {
                            gameObject.transform.localRotation = updateInfo.updateData.rotation;
                        }
                        if (updateInfo.data.enableScale)
                        {
                            gameObject.transform.localScale = updateInfo.updateData.scale;
                        }
                    }
                }
            }
        }
    }
}