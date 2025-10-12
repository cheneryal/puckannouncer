// --- Step 1: Add necessary using statements ---
using UnityEngine;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Unity.Collections; // Required for FixedString types
using Unity.Netcode; // Required for network client checks

// --- Step 2: Define your mod's namespace and main class ---
namespace AnnouncerMod
{
    /// <summary>
    /// A simple static logger to write AnnouncerMod specific messages to its own file.
    /// </summary>
    internal static class AnnouncerLogger
    {
        private static StreamWriter logWriter;

        static AnnouncerLogger()
        {
            try
            {
                string puckGamePath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string logDirectory = Path.Combine(puckGamePath, "Logs");

                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                string logFilePath = Path.Combine(logDirectory, "AnnouncerMod.log");
                logWriter = new StreamWriter(logFilePath, true) { AutoFlush = true };
                Log("Logger initialized successfully.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnnouncerMod] Failed to initialize file logger: {e.Message}");
                logWriter = null;
            }
        }

        public static void Log(string message)
        {
            Debug.Log($"[AnnouncerMod] {message}");
            logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }
        
        public static void Close()
        {
            logWriter?.Close();
            logWriter = null;
        }
    }
    
    /// <summary>
    /// Component attached to the puck to determine if it's heading towards a net.
    /// </summary>
    internal class PuckTargetingSystem : MonoBehaviour
    {
        private const int CHECK_EVERY_X_FRAMES = 4;
        private readonly float MAX_DISTANCE = 50f;
        private readonly LayerMask _goalTriggerLayerMask = LayerMask.GetMask("Goal Trigger");
        private readonly float PUCK_RADIUS_OFFSET = 0.06f; 
        private readonly Vector3 TOP_RAY_OFFSET = new Vector3(0, 0.08f, 0); 
        private readonly Vector3 BOTTOM_RAY_OFFSET = new Vector3(0, 0.01f, 0);

        private Vector3 _lastPosition;
        private int _frameCount;

        internal Dictionary<PlayerTeam, bool> IsHeadedTowardsNet { get; } = new Dictionary<PlayerTeam, bool>
        {
            { PlayerTeam.Blue, false },
            { PlayerTeam.Red, false },
        };

        void Start()
        {
            _lastPosition = transform.position;
            _frameCount = 0;
        }

        void FixedUpdate()
        {
            if (++_frameCount >= CHECK_EVERY_X_FRAMES)
            {
                foreach (var team in IsHeadedTowardsNet.Keys.ToList())
                {
                    IsHeadedTowardsNet[team] = false;
                }
                
                CheckTrajectory();
                _lastPosition = transform.position;
                _frameCount = 0;
            }
        }

        private void CheckTrajectory()
        {
            Vector3 direction = (transform.position - _lastPosition).normalized;
            if (direction == Vector3.zero) return;

            Vector3 currentPosition = transform.position;
            Vector3 leftVector = currentPosition + (Quaternion.Euler(0, -90, 0) * direction * PUCK_RADIUS_OFFSET);
            Vector3 rightVector = currentPosition + (Quaternion.Euler(0, 90, 0) * direction * PUCK_RADIUS_OFFSET);
            
            RaycastHit hit;
            
            bool hitDetected = 
                Physics.Raycast(new Ray(leftVector + TOP_RAY_OFFSET, direction), out hit, MAX_DISTANCE, _goalTriggerLayerMask, QueryTriggerInteraction.Collide) ||
                Physics.Raycast(new Ray(rightVector + TOP_RAY_OFFSET, direction), out hit, MAX_DISTANCE, _goalTriggerLayerMask, QueryTriggerInteraction.Collide) ||
                Physics.Raycast(new Ray(leftVector + BOTTOM_RAY_OFFSET, direction), out hit, MAX_DISTANCE, _goalTriggerLayerMask, QueryTriggerInteraction.Collide) ||
                Physics.Raycast(new Ray(rightVector + BOTTOM_RAY_OFFSET, direction), out hit, MAX_DISTANCE, _goalTriggerLayerMask, QueryTriggerInteraction.Collide);

            if (hitDetected)
            {
                GoalTrigger goalTrigger = hit.collider.GetComponent<GoalTrigger>();
                if (goalTrigger != null)
                {
                    var goalField = AccessTools.Field(typeof(GoalTrigger), "goal");
                    if (goalField != null)
                    {
                        Goal goalObject = (Goal)goalField.GetValue(goalTrigger);
                        if (goalObject != null)
                        {
                            var goalTeamField = AccessTools.Field(typeof(Goal), "Team");
                            if(goalTeamField != null)
                            {
                                PlayerTeam team = (PlayerTeam)goalTeamField.GetValue(goalObject);
                                IsHeadedTowardsNet[team] = true;
                            }
                        }
                    }
                }
            }
        }
    }


    public class Announcer : IPuckMod
    {
        private static readonly Harmony harmony = new Harmony("com.yourname.announcermod");
        private static readonly System.Random random = new System.Random();

        // --- CONFIGURATION & DEBUG SWITCHES ---
        private static readonly bool BroadcastToChat = false;
        private static readonly bool EnableDetailedDebugLogging = false;
        
        // --- File Paths ---
        private static readonly string gameFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Puck", "textfiles");
        private static readonly string goalScorerFile = Path.Combine(gameFilesPath, "goal_scorer.txt");
        private static readonly string assistPlayerFile = Path.Combine(gameFilesPath, "goal_assister.txt");
        private static readonly string secondAssistPlayerFile = Path.Combine(gameFilesPath, "goal_assister2.txt");

        // --- VOCABULARY EXPANSION ---
        private static readonly List<string> possessionPhrases = new List<string> { "{0} has it.", "{0} with the puck.", "{0} controls it.", "Here's {0}.", "{0} picks it up." };
        private static readonly List<string> zoneEntryPhrases = new List<string> { "carries it over the {1}", "brings it across the {1}", "gains the {1}"};
        private static readonly List<string> shotPhrases = new List<string> { "{0} shoots!", "{0} fires!", "A shot from {0}!", "Lets one go!" };
        private static readonly List<string> savePhrases = new List<string> { "Great save by {0}!", "Turned aside by {0}!", "{0} makes the stop!", "Denied by {0}!", "Kicked out by {0}." };
        private static readonly List<string> passPhrases = new List<string> { "Passes to {0}!", "Moves it to {0}!", "Finds {0}.", "Over to {0}." };
        private static readonly List<string> stripPhrases = new List<string> { "{0} strips the puck!", "{0} steals it!", "Taken away by {0}!" };
        private static readonly List<string> interceptionPhrases = new List<string> { "{0} intercepts!", "{0} picks that one off." };
        private static readonly List<string> boardBattlePhrases = new List<string> { "Battle along the boards!", "Tied up on the boards.", "Pinned against the wall." };
        private static readonly List<string> blockedShotPhrases = new List<string> { "Blocked by {0}!", "Shot blocked!", "{0} gets in the lane!"};
        private static readonly List<string> defensiveZoneClearPhrases = new List<string> { "{0} clears the zone.", "{0} gets it out of the zone."};

        // --- State Tracking Variables ---
        internal static Player lastPlayerWithPuck = null;
        internal static int lastAnnouncedPlayerId = -1;
        internal static float lastPossessionChangeTime = 0f;
        internal static float possessionStartTime = 0f;
        internal static Vector3 lastPuckPosition;
        internal static PlayerTeam lastPlayerWithPuckTeam = PlayerTeam.None;
        internal static Goal redGoal, blueGoal;
        internal static GameState lastKnownGameState;
        internal static bool isFirstCheck = true;
        internal static PuckTargetingSystem puckTargetingSystem;
        internal static Dictionary<int, float> lastPuckTouchTime = new Dictionary<int, float>();
        internal static bool wasPuckFreeInLastFrame = true;
        internal static bool isRedTeamInPositiveZ = true;
        internal static bool isOrientationSet = false;
        internal static bool isPuckTravelingAfterShot = false;
        internal static bool isWaitingForFaceoffWin = false;
        internal static Player potentialShooter;
        internal static Player potentialSaver;
        internal static float shotTime;
        internal static bool hasAnnouncedZoneEntryThisPossession = false;
        internal static bool isAnnouncingBoardBattle = false;
        internal static float boardBattleStartTime = 0f;

        // --- Thresholds and Timings ---
        private const float shotSpeedThreshold = 18f;
        private const float directTakeawayTime = 0.3f;
        private const float interceptionTimeWindow = 1.5f; 
        private const float minPossessionTimeForAnnouncement = 0.3f;
        private const float maxTippedMilliseconds = 150f; 
        private const float boardBattleTimeThreshold = 2.0f;
        private const float saveCheckDelay = 0.75f;
        
        // --- Commentary Cooldown System ---
        private static float commentaryCooldown = 0f;
        private enum CommentaryPriority { Low, Medium, High }
        private static readonly Dictionary<CommentaryPriority, float> CooldownDurations = new Dictionary<CommentaryPriority, float>
        {
            { CommentaryPriority.Low, 1.5f },
            { CommentaryPriority.Medium, 2.5f },
            { CommentaryPriority.High, 4.0f }
        };

        public bool OnEnable()
        {
            try
            {
                harmony.PatchAll(typeof(Announcer).Assembly);
                isFirstCheck = true;
                AnnouncerLogger.Log("AnnouncerMod Enabled and Patches Applied!");
            }
            catch (Exception e) { AnnouncerLogger.Log($"AnnouncerMod failed to patch: {e}"); return false; }
            return true;
        }

        public bool OnDisable()
        {
            try
            {
                harmony.UnpatchSelf();
                AnnouncerLogger.Log("AnnouncerMod Disabled and Patches Reverted.");
                AnnouncerLogger.Close();
            }
            catch (Exception e) { AnnouncerLogger.Log($"AnnouncerMod failed to unpatch: {e.Message}"); return false; }
            return true;
        }

        private static void InitializeRinkOrientation()
        {
            Goal[] goals = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
            if (goals == null || goals.Length < 2) return;

            var teamField = AccessTools.Field(typeof(Goal), "Team");
            if (teamField == null) { AnnouncerLogger.Log("Error: Could not access 'Team' field in Goal class."); return; }

            foreach (Goal goal in goals)
            {
                PlayerTeam team = (PlayerTeam)teamField.GetValue(goal);
                if (team == PlayerTeam.Red)
                {
                    redGoal = goal;
                    isRedTeamInPositiveZ = goal.transform.position.z > 0;
                }
                else if (team == PlayerTeam.Blue) { blueGoal = goal; }
            }

            if (redGoal != null && blueGoal != null)
            {
                isOrientationSet = true;
                AnnouncerLogger.Log($"Rink orientation set: Red Team is in Positive Z: {isRedTeamInPositiveZ}");
            }
        }

        private static string GetPlayerDisplayName(Player player)
        {
            if (player == null) return "a player";
            return player.Username.Value.ToString();
        }
        
        private static string GetZoneFromZ(float z_pos)
        {
             if (Mathf.Abs(z_pos) < 25.0f) return "Neutral";
             if (z_pos > 0) return isRedTeamInPositiveZ ? "Red" : "Blue";
             return isRedTeamInPositiveZ ? "Blue" : "Red";
        }

        private static string GetTeamZoneName(PlayerTeam team)
        {
            if (!isOrientationSet) return "Neutral";
            if (team == PlayerTeam.Red) return GetZoneFromZ(redGoal.transform.position.z);
            if (team == PlayerTeam.Blue) return GetZoneFromZ(blueGoal.transform.position.z);
            return "Neutral";
        }

        private static string GetFaceoffLocationName(Vector3 faceoffPosition)
        {
            if (Mathf.Abs(faceoffPosition.z) < 1.0f) return "center ice";
            if (Mathf.Abs(Mathf.Abs(faceoffPosition.z) - 25.0f) < 2.0f) return "the blue line";
            if (Mathf.Abs(faceoffPosition.z) > 40.0f)
            {
                string zone = GetZoneFromZ(faceoffPosition.z);
                string side = faceoffPosition.x > 0 ? "right" : "left";
                return $"the {side}-side faceoff circle in the {zone} zone";
            }
            return "the neutral zone";
        }
        
        internal static void RunCommentary(Puck puck)
        {
            if (puck == null || GameManager.Instance?.GameState == null || puckTargetingSystem == null) return;
            if (GameManager.Instance.GameState.Value.Phase != GamePhase.Playing) { ResetPlayState(); return; }
            if (!isOrientationSet) InitializeRinkOrientation();
            if (commentaryCooldown > 0) { commentaryCooldown -= Time.fixedDeltaTime; return; }

            string comment = null;
            CommentaryPriority priority = CommentaryPriority.Low;

            var collisions = puck.GetPlayerCollisions();
            Player playerWithPuck = collisions?.FirstOrDefault(c => c.Key != null && c.Key.Role.Value != PlayerRole.Goalie).Key;
            
            int currentPlayerId = playerWithPuck != null ? playerWithPuck.GetInstanceID() : -1;
            int lastPlayerId = lastPlayerWithPuck != null ? lastPlayerWithPuck.GetInstanceID() : -1;
            
            if (potentialSaver != null && Time.time - shotTime > saveCheckDelay)
            {
                comment = string.Format(savePhrases[random.Next(savePhrases.Count)], GetPlayerDisplayName(potentialSaver));
                priority = CommentaryPriority.High;
                ResetShotState();
            }
            else if (isWaitingForFaceoffWin && playerWithPuck != null)
            {
                comment = $"{GetPlayerDisplayName(playerWithPuck)} wins the draw!";
                priority = CommentaryPriority.Medium;
                isWaitingForFaceoffWin = false;
            }
            else if (isPuckTravelingAfterShot)
            {
                var goalieCollision = collisions?.FirstOrDefault(c => c.Key != null && c.Key.Role.Value == PlayerRole.Goalie);
                if (goalieCollision.Value.Key != null && potentialSaver == null)
                {
                    potentialSaver = goalieCollision.Value.Key;
                }
                else
                {
                    var playerCollision = collisions?.FirstOrDefault(c => c.Key != null && c.Key.Role.Value != PlayerRole.Goalie);
                    if (playerCollision.Value.Key != null && potentialShooter != null && playerCollision.Value.Key.Team.Value != potentialShooter.Team.Value)
                    {
                        comment = string.Format(blockedShotPhrases[random.Next(blockedShotPhrases.Count)], GetPlayerDisplayName(playerCollision.Value.Key));
                        priority = CommentaryPriority.Medium;
                        ResetShotState();
                    }
                }
                if (puck.PredictedSpeed < shotSpeedThreshold / 2) ResetShotState();
            }
            // --- SHOT ATTEMPT LOGIC ---
            else if (playerWithPuck != null && puck.PredictedSpeed > shotSpeedThreshold)
            {
                PlayerTeam opponentTeam = playerWithPuck.Team.Value == PlayerTeam.Red ? PlayerTeam.Blue : PlayerTeam.Red;
                if (puckTargetingSystem.IsHeadedTowardsNet[opponentTeam])
                {
                    comment = string.Format(shotPhrases[random.Next(shotPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                    priority = CommentaryPriority.Medium;
                    isPuckTravelingAfterShot = true;
                    potentialShooter = playerWithPuck;
                    shotTime = Time.time;
                }
            }
            // --- POSSESSION CHANGE LOGIC ---
            else if (playerWithPuck != null && currentPlayerId != lastPlayerId)
            {
                // Player-to-Player possession change
                if (lastPlayerWithPuck != null) 
                {
                    if (playerWithPuck.Team.Value == lastPlayerWithPuck.Team.Value)
                    {
                        comment = string.Format(passPhrases[random.Next(passPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                        priority = CommentaryPriority.Low;
                    }
                    else // Direct takeaway from opponent
                    {
                        comment = string.Format(stripPhrases[random.Next(stripPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                        priority = CommentaryPriority.Medium;
                    }
                }
                // Free Puck-to-Player possession change
                else 
                {
                    float timeSinceLastTouch = Time.time - lastPossessionChangeTime;
                    if(lastPlayerWithPuckTeam != PlayerTeam.None && playerWithPuck.Team.Value != lastPlayerWithPuckTeam && timeSinceLastTouch < interceptionTimeWindow)
                    {
                        comment = string.Format(interceptionPhrases[random.Next(interceptionPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                        priority = CommentaryPriority.Medium;
                    }
                    else
                    {
                        comment = string.Format(possessionPhrases[random.Next(possessionPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                        priority = CommentaryPriority.Low;
                    }
                }
            }
            // --- SUSTAINED POSSESSION LOGIC ---
            else if (playerWithPuck != null && currentPlayerId == lastPlayerId && !wasPuckFreeInLastFrame)
            {
                string currentZone = GetZoneFromZ(puck.transform.position.z);
                string previousZone = GetZoneFromZ(lastPuckPosition.z);
                
                if (currentZone != previousZone && !hasAnnouncedZoneEntryThisPossession)
                {
                    string defensiveZone = GetTeamZoneName(playerWithPuck.Team.Value);

                    // A defensive player is clearing their own zone
                    if (previousZone == defensiveZone && currentZone == "Neutral")
                    {
                        comment = string.Format(defensiveZoneClearPhrases[random.Next(defensiveZoneClearPhrases.Count)], GetPlayerDisplayName(playerWithPuck));
                        priority = CommentaryPriority.Medium;
                        hasAnnouncedZoneEntryThisPossession = true;
                    }
                    // An attacking player is entering the offensive zone from the neutral zone
                    else if (previousZone == "Neutral" && currentZone != "Neutral" && currentZone != defensiveZone)
                    {
                        comment = $"{GetPlayerDisplayName(playerWithPuck)} {string.Format(zoneEntryPhrases[random.Next(zoneEntryPhrases.Count)], "", "blue line")}";
                        priority = CommentaryPriority.Medium;
                        hasAnnouncedZoneEntryThisPossession = true;
                    }
                    // An attacking player crosses the center line
                    else if (previousZone != "Neutral" && currentZone != "Neutral" && previousZone != currentZone)
                    {
                         comment = $"{GetPlayerDisplayName(playerWithPuck)} {string.Format(zoneEntryPhrases[random.Next(zoneEntryPhrases.Count)], "", "center line")}";
                         priority = CommentaryPriority.Medium;
                         hasAnnouncedZoneEntryThisPossession = true;
                    }
                }
                else if (Mathf.Abs(playerWithPuck.transform.position.x) > 20f)
                {
                    if (boardBattleStartTime == 0f) { boardBattleStartTime = Time.time; } 
                    else if (!isAnnouncingBoardBattle && Time.time - boardBattleStartTime > boardBattleTimeThreshold) 
                    {
                        comment = boardBattlePhrases[random.Next(boardBattlePhrases.Count)];
                        priority = CommentaryPriority.Low;
                        isAnnouncingBoardBattle = true;
                    }
                }
            }
            
            if (playerWithPuck == null || Mathf.Abs(playerWithPuck.transform.position.x) < 20f) { boardBattleStartTime = 0f; isAnnouncingBoardBattle = false; }
            if (comment != null) { lastAnnouncedPlayerId = currentPlayerId; Broadcast(comment, priority); }
            
            // --- State Update Logic ---
            if (playerWithPuck != null && lastPlayerId != currentPlayerId)
            {
                possessionStartTime = Time.time;
                hasAnnouncedZoneEntryThisPossession = false;
            }
            else if (playerWithPuck == null && lastPlayerWithPuck != null)
            {
                lastPossessionChangeTime = Time.time;
            }
            
            if (currentPlayerId != lastPlayerId)
            {
                lastPlayerWithPuck = playerWithPuck;
                if (playerWithPuck != null) lastPlayerWithPuckTeam = playerWithPuck.Team.Value;
            }
            
            wasPuckFreeInLastFrame = (playerWithPuck == null);
        }

        private static void Broadcast(string message, CommentaryPriority priority = CommentaryPriority.Low)
        {
            if (string.IsNullOrEmpty(message)) return;
            AnnouncerLogger.Log($"BROADCAST: {message}");
            if (NetworkManager.Singleton?.IsClient != true) return;
            if (BroadcastToChat) { UIChat.Instance?.SendMessage(message); }
            else { UIToastManager.Instance?.ShowToast("announcer_commentary", message, 3f); }
            commentaryCooldown = CooldownDurations[priority];
        }

        private static void ResetShotState()
        {
            isPuckTravelingAfterShot = false;
            potentialShooter = null;
            potentialSaver = null;
            shotTime = 0f;
        }

        private static void ResetPlayState()
        {
            ResetShotState();
            lastPlayerWithPuck = null;
            lastAnnouncedPlayerId = -1;
            isWaitingForFaceoffWin = false;
            wasPuckFreeInLastFrame = true;
            boardBattleStartTime = 0f;
            isAnnouncingBoardBattle = false;
            hasAnnouncedZoneEntryThisPossession = false;
        }

        // --- Harmony Patches ---

        [HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
        public class Puck_OnCollisionEnter_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Collision collision)
            {
                var stick = collision.gameObject.GetComponent<Stick>();
                if (stick != null && stick.Player != null)
                {
                    lastPuckTouchTime[stick.Player.GetInstanceID()] = Time.time;
                }
            }
        }

        [HarmonyPatch(typeof(Puck), "FixedUpdate")]
        public class Puck_FixedUpdate_Patch
        {
            public static void Postfix(Puck __instance)
            {
                if (__instance != null && puckTargetingSystem == null)
                {
                    puckTargetingSystem = __instance.gameObject.GetComponent<PuckTargetingSystem>() ?? __instance.gameObject.AddComponent<PuckTargetingSystem>();
                }

                if (GameManager.Instance?.GameState != null)
                {
                    GameState currentState = GameManager.Instance.GameState.Value;

                    if (isFirstCheck) { lastKnownGameState = currentState; isFirstCheck = false; }
                    else
                    {
                        if (currentState.Phase != lastKnownGameState.Phase)
                        {
                            AnnouncerLogger.Log($"Game phase changed from {lastKnownGameState.Phase} to {currentState.Phase}");
                            if (currentState.Phase == GamePhase.FaceOff)
                            {
                                ResetPlayState();
                                lastPuckTouchTime.Clear(); // Clear touch history on faceoff
                                
                                // Find an attacker to determine faceoff location, as the puck may be despawned.
                                Player faceoffPlayer = UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None)
                                    .FirstOrDefault(p => p.Role.Value == PlayerRole.Attacker);

                                // Use the attacker's position, or fall back to the puck's last known position.
                                Vector3 faceoffPosition = (faceoffPlayer != null)
                                    ? faceoffPlayer.transform.position
                                    : __instance.transform.position; 

                                string faceoffLocation = GetFaceoffLocationName(faceoffPosition);
                                Broadcast($"Faceoff at {faceoffLocation}!", CommentaryPriority.Medium);
                                isWaitingForFaceoffWin = true;
                            }
                        }

                        if (currentState.RedScore > lastKnownGameState.RedScore || currentState.BlueScore > lastKnownGameState.BlueScore)
                        {
                            ResetPlayState();
                            string scorer = File.Exists(goalScorerFile) ? File.ReadAllText(goalScorerFile).Trim() : "";
                            string assister = File.Exists(assistPlayerFile) ? File.ReadAllText(assistPlayerFile).Trim() : "";
                            string secondAssister = File.Exists(secondAssistPlayerFile) ? File.ReadAllText(secondAssistPlayerFile).Trim() : "";
                            
                            string teamScored = (currentState.RedScore > lastKnownGameState.RedScore) ? "Red" : "Blue";
                            string goalComment = $"GOAL for the {teamScored} team! The score is now {currentState.RedScore} to {currentState.BlueScore}.";

                            if (!string.IsNullOrEmpty(scorer))
                            {
                                goalComment = $"GOAL! Scored by {scorer}. The score is {currentState.RedScore} to {currentState.BlueScore}.";
                                if (!string.IsNullOrEmpty(assister))
                                {
                                    goalComment += $" Assist to {assister}";
                                    if (!string.IsNullOrEmpty(secondAssister)) goalComment += $", with the secondary from {secondAssister}.";
                                    else goalComment += ".";
                                  }
                            }
                            
                            Broadcast(goalComment, CommentaryPriority.High);
                        }
                        
                        lastKnownGameState = currentState;
                    }
                }
                
                try { RunCommentary(__instance); }
                catch (Exception e) { AnnouncerLogger.Log($"Error in RunCommentary: {e}"); }
                finally { lastPuckPosition = __instance.transform.position; }
            }
        }
    }
}

