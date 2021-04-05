/*
 * Unity Wakapi Support
 * Wakapi logging for the Unity Editor.
 *
 * Version
 * v0.1
 * Author:
 * github.com/LoneDev6
 * 
 * Original author:
 * Matt Bengston @bengsfort <bengston.matthew@gmail.com>
 */

namespace LoneDev
{
    using System;
    using System.IO;
    using System.Diagnostics;
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Networking;
    using UnityEngine.SceneManagement;
    using UnityEditor;
    using UnityEditor.Callbacks;
    using UnityEditor.SceneManagement;

    [InitializeOnLoad]
    public static class Wakapi
    {
        /// <summary>
        /// The current plugin version.
        /// </summary>
        public const double Version = 0.1;

        #region properties
        
        /// <summary>
        /// The minimum time required to elapse before sending normal heartbeats.
        /// </summary>
        public const Int32 HeartbeatBuffer = 120;
        

        /// <summary>
        /// Whether the plugin is enabled or not.
        /// </summary>
        public static bool Enabled
        {
            get { return EditorPrefs.GetBool("Wakapi_Enabled", false); }
            set { EditorPrefs.SetBool("Wakapi_Enabled", value); }
        }

        /// <summary>
        /// Should version control be used?
        /// </summary>
        public static bool EnableVersionControl
        {
            get { return EditorPrefs.GetBool("Wakapi_GitEnabled", true); }
            set { EditorPrefs.SetBool("Wakapi_GitEnabled", value); }
        }

        /// <summary>
        /// The users API Key.
        /// </summary>
        public static string ApiKey
        {
            get { return EditorPrefs.GetString("Wakapi_ApiKey", ""); }
            set { EditorPrefs.SetString("Wakapi_ApiKey", value); }
        }
        
        public static string BaseURL
        {
            get { return EditorPrefs.GetString("Wakapi_URL", ""); }
            set { EditorPrefs.SetString("Wakapi_URL", value); }
        }
        
        public static string HeartbeatURL => $"{BaseURL}/api/heartbeat?api_key={ApiKey}";

        /// <summary>
        /// The project to log time against.
        /// </summary>
        public static String ActiveProject
        {
            get
            {
                return EditorPrefs.GetString("Wakapi_ActiveProject", "UnityProject");
            }
            set
            {
                EditorPrefs.SetString("Wakapi_ActiveProject", value);
            }
        }

        /// <summary>
        /// The last heartbeat sent.
        /// </summary>
        private static HeartbeatResponseSchema s_LastHeartbeat;

        #endregion

        static Wakapi()
        {
            if (!Enabled)
                return;

            ActiveProject = Application.productName;

            // Initialize with a heartbeat
            PostHeartbeat();

            // Frame callback
            EditorApplication.update += OnUpdate;
            LinkCallbacks();
        }

        #region EventHandlers

        /// <summary>
        /// Callback that fires every frame.
        /// </summary>
        static void OnUpdate()
        {
            AsyncHelper.Execute();
        }

        /// <summary>
        /// Detect when scripts are reloaded and restart heartbeats
        /// </summary>
        [DidReloadScripts()]
        static void OnScriptReload()
        {
            PostHeartbeat();
            // Relink all of our callbacks
            LinkCallbacks(true);
        }

        /// <summary>
        /// Send a heartbeat every time the user enters or exits play mode.
        /// </summary>
        static void OnPlaymodeStateChanged()
        {
            PostHeartbeat();
        }

        /// <summary>
        /// Send a heartbeat every time the user clicks on the context menu.
        /// </summary>
        static void OnPropertyContextMenu(GenericMenu menu, SerializedProperty property)
        {
            PostHeartbeat();
        }

        /// <summary>
        /// Send a heartbet everytime the hierarchy changes.
        /// </summary>
        static void OnHierarchyWindowChanged()
        {
            PostHeartbeat();
        }

        /// <summary>
        /// Send a heartbeat every time the scene is saved.
        /// </summary>
        static void OnSceneSaved(Scene scene)
        {
            PostHeartbeat(true);
        }

        /// <summary>
        /// Send a heartbeat every time a scene is opened.
        /// </summary>
        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            PostHeartbeat();
        }

        /// <summary>
        /// Send a heartbeat every time a scene is closed.
        /// </summary>
        static void OnSceneClosing(Scene scene, bool removingScene)
        {
            PostHeartbeat();
        }

        /// <summary>
        /// Send a heartbeat every time a scene is created.
        /// </summary>
        static void OnSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            PostHeartbeat();
        }

        #endregion

        #region ApiCalls

        /// <summary>
        /// Sends a heartbeat to the Wakapi API.
        /// </summary>
        /// <param name="fromSave">Was this triggered from a save?</param>
        static void PostHeartbeat(bool fromSave = false)
        {
            // Create our heartbeat
            // If the current scene is empty it's an unsaved scene; so don't
            // try to determine exact file position in that instance.
            var currentScene = EditorSceneManager.GetActiveScene().path;
            var heartbeat = new HeartbeatSchema(
                currentScene != string.Empty
                    ? Path.Combine(
                        Application.dataPath,
                        currentScene.Substring("Assets/".Length)
                    )
                    : string.Empty,
                fromSave
            );

            // If it hasn't been longer than the last heartbeat buffer, ignore if
            // the heartbeat isn't triggered by a save or the scene changing.
            if ((heartbeat.time - s_LastHeartbeat.time < HeartbeatBuffer) && !fromSave
                                                                          && (heartbeat.entity == s_LastHeartbeat.entity))
                return;

            var heartbeatJson = $"[{JsonUtility.ToJson(heartbeat)}]";
            var www = UnityWebRequest.Post(HeartbeatURL, string.Empty);
            // Manually add an upload handler so the data isn't corrupted
            www.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(heartbeatJson));
            // Set the content type to json since it defaults to www form data
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", $"Basic {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ApiKey))}");
            www.SetRequestHeader("X-Machine-Name", heartbeat.machine);
            www.SetRequestHeader("User-Agent",
                $"wakatime/1.0.0 ({heartbeat.operating_system}-idk) {heartbeat.editor}/1.0.0 {heartbeat.editor}-wakatime/1.0.0");

            // Send the request
            AsyncHelper.Add(new RequestEnumerator(www.Send(), () => {
                var result = JsonUtility.FromJson<ResponseSchema<HeartbeatResponseSchema>>(www.downloadHandler.text);

                if (result.error != null)
                {
                    UnityEngine.Debug.LogError(
                        "<Wakapi> Failed to send heartbeat to Wakapi. If this " +
                        "continues there is something wrong with your API key, URL or server is offline.\n" + result.error
                    );
                }
                else
                {
                    // UnityEngine.Debug.Log("Sent heartbeat to Wakapi");
                    s_LastHeartbeat = result.data;
                }
            }));
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Subscribes the plugin event handlers to the editor events.
        /// </summary>
        /// <param name="clean">Should we remove old callbacks before linking?</param>
        static void LinkCallbacks(bool clean = false)
        {
            // Remove old callbacks before adding them back again
            if (clean)
            {
                // Scene modification callbacks
                EditorApplication.playmodeStateChanged -= OnPlaymodeStateChanged;
                EditorApplication.contextualPropertyMenu -= OnPropertyContextMenu;
                EditorApplication.hierarchyWindowChanged -= OnHierarchyWindowChanged;
                // Scene state callbacks
                EditorSceneManager.sceneSaved -= OnSceneSaved;
                EditorSceneManager.sceneOpened -= OnSceneOpened;
                EditorSceneManager.sceneClosing -= OnSceneClosing;
                EditorSceneManager.newSceneCreated -= OnSceneCreated;
            }

            // Scene modification callbacks
            EditorApplication.playmodeStateChanged += OnPlaymodeStateChanged;
            EditorApplication.contextualPropertyMenu += OnPropertyContextMenu;
            EditorApplication.hierarchyWindowChanged += OnHierarchyWindowChanged;
            // Scene state callbacks
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.newSceneCreated += OnSceneCreated;
        }

        #endregion

        #region PreferencesView

        /// <summary>
        /// Render function for the preferences view.
        /// </summary>
        [PreferenceItem("Wakapi")]
        static void WakapiPreferencesView()
        {
            if (EditorApplication.isCompiling)
            {
                EditorGUILayout.HelpBox(
                    "Hold up!\n" +
                    "Unity is compiling right now, so to prevent catastrophic " +
                    "failure of something you'll have to try again once it's done.",
                    MessageType.Warning
                );
                return;
            }

            // Plugin Meta
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(string.Format("v{0:0.0} by LoneDev (original author @bengsfort)", Version), MessageType.Info);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Separator();

            // Main integration settings
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("Integration Settings", EditorStyles.boldLabel);

            // Don't show the rest of the items if its not even enabled
            Enabled = EditorGUILayout.BeginToggleGroup("Enable Wakapi", Enabled);

            EditorGUILayout.Separator();
            EditorGUILayout.Separator();

            // Should version control be enabled?
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Enable Version Control");
            EnableVersionControl = EditorGUILayout.Toggle(EnableVersionControl);
            EditorGUILayout.EndHorizontal();

            // BaseURl Key field
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Base URL");
            EditorGUILayout.BeginVertical();
            var newBaseURL = EditorGUILayout.TextField(BaseURL);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            
            // API Key field
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("API Key");
            EditorGUILayout.BeginVertical();
            var newApiKey = EditorGUILayout.PasswordField(ApiKey);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Separator();

            // Git information
            if (Enabled && EnableVersionControl)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Currently on branch: " + GitHelper.branch);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndToggleGroup();
            EditorGUILayout.EndVertical();

            // Handle any changed data
            if (GUI.changed)
            {
                BaseURL = newBaseURL;
                ApiKey = newApiKey;
            }
        }

        #endregion

        #region HelperClasses

        /// <summary>
        /// A container class for async request enumerators and their 'done' callbacks.
        /// </summary>
        public class RequestEnumerator
        {
            /// <summary>
            /// The initialized enumerator for this request.
            /// </summary>
            public IEnumerator status;

            /// <summary>
            /// The operation representing this request.
            /// </summary>
            AsyncOperation m_Request;

            /// <summary>
            /// The callback to be fired on completion of the request.
            /// </summary>
            Action m_Callback;

            /// <summary>
            /// Instantiates an enumerator that waits for the request to finish.
            /// </summary>
            public IEnumerator Start()
            {
                while (!m_Request.isDone)
                {
                    // Not done yet..
                    yield return null;
                }

                // It's done!
                m_Callback();
            }

            public RequestEnumerator(AsyncOperation req, Action cb)
            {
                m_Request = req;
                m_Callback = cb;
                status = Start();
            }
        }

        /// <summary>
        /// A Helper class for dealing with async web calls since coroutines are
        /// not really an option in edit mode.
        /// </summary>
        public static class AsyncHelper
        {
            /// <summary>
            /// A queue of async request enumerators.
            /// </summary>
            private static readonly Queue<RequestEnumerator> s_Requests = new Queue<RequestEnumerator>();

            /// <summary>
            /// Adds a request enumerator to the queue.
            /// </summary>
            public static void Add(RequestEnumerator action)
            {
                lock (s_Requests)
                {
                    s_Requests.Enqueue(action);
                }
            }

            /// <summary>
            /// If there are queued requests, it will dequeue and fire them. If
            /// the request is not finished, it will be added back to the queue.
            /// </summary>
            public static void Execute()
            {
                if (s_Requests.Count > 0)
                {
                    RequestEnumerator action = null;
                    lock (s_Requests)
                    {
                        action = s_Requests.Dequeue();
                    }

                    // Re-queue the action if it is not complete
                    if (action.status.MoveNext())
                    {
                        Add(action);
                    }
                }
            }
        }

        #endregion

        #region ApiSchemas

        /// <summary>
        /// Generic API response object with configurable data type.
        /// </summary>
        [Serializable]
        public struct ResponseSchema<T>
        {
            public string error;
            public T data;

            public override string ToString()
            {
                return
                    "UserResponseObject:\n" +
                    "\terror: " + error + "\n" +
                    "\tdata: " + data.ToString();
            }
        }

        /// <summary>
        /// User schema from Wakapi API.
        /// </summary>
        [Serializable]
        public struct CurrentUserSchema
        {
            public string username;
            public string display_name;
            public string full_name;
            public string id;
            public string photo;
            public string last_plugin; // used for debugging
            public string last_heartbeat; // used for debugging

            public override string ToString()
            {
                return
                    "CurrentUserSchema:\n" +
                    "\tusername: " + username + "\n" +
                    "\tdisplay_name: " + display_name + "\n" +
                    "\tfull_name: " + full_name + "\n" +
                    "\tid: " + id + "\n" +
                    "\tphoto: " + photo;
            }
        }

        /// <summary>
        /// Project schema from Wakapi API.
        /// </summary>
        [Serializable]
        public struct ProjectSchema
        {
            public string id;
            public string name;

            public override string ToString()
            {
                return "ProjectSchema:\n" +
                       "\tid: " + id + "\n" +
                       "\tname: " + name + "\n";
            }
        }

        /// <summary>
        /// Heartbeat response schema from Wakapi API.
        /// </summary>
        [Serializable]
        public struct HeartbeatResponseSchema
        {
            public string id;
            public string entity;
            public string type;
            public Int32 time;
        }

        /// <summary>
        /// Schema for heartbeat postdata.
        /// </summary>
        [Serializable]
        public struct HeartbeatSchema
        {
            // default to current scene?
            public string entity;

            // type of entity (app)
            public string type;

            public string category;

            public string project;

            // version control branch
            public string branch;

            // language (unity)
            public string language;

            // is this triggered by saving a file?
            public bool is_write;

            public string editor;
            public string operating_system;

            public string machine;

            // unix epoch timestamp
            public Int32 time;

            public HeartbeatSchema(string file, bool save = false)
            {
                entity = (file == string.Empty ? "Unsaved Scene" : file);
                type = "app";
                time = (Int32) (DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                project = ActiveProject;
                branch = GitHelper.branch;
                language = "Unity";
                is_write = save;
                category = null;
                editor = "Unity";
                if(SystemInfo.operatingSystem.StartsWith(OperatingSystemFamily.Windows.ToString()))
                    operating_system = OperatingSystemFamily.Windows.ToString();
                else if(SystemInfo.operatingSystem.StartsWith(OperatingSystemFamily.Linux.ToString()))
                    operating_system = OperatingSystemFamily.Linux.ToString();
                else if(SystemInfo.operatingSystem.StartsWith(OperatingSystemFamily.MacOSX.ToString()))
                    operating_system = OperatingSystemFamily.MacOSX.ToString();
                else
                    operating_system = OperatingSystemFamily.Other.ToString();
                machine = SystemInfo.deviceName;
            }
        }

        #endregion

        #region VersionControl

        static class GitHelper
        {
            public static string branch
            {
                get
                {
                    if (EnableVersionControl)
                        return GetCurrentBranch();
                    else
                        return "master";
                }
            }

            static string GetGitPath()
            {
                var pathVar = Process.GetCurrentProcess().StartInfo.EnvironmentVariables["PATH"];
                var pathSeparator = (Application.platform == RuntimePlatform.WindowsEditor ? ';' : ':');
                var paths = pathVar.Split(new char[] {pathSeparator});
                foreach (string path in paths)
                {
                    if (File.Exists(Path.Combine(path, "git")))
                        return path;
                }

                return String.Empty;
            }

            static string GetCurrentBranch()
            {
                var path = GetGitPath();

                if (string.IsNullOrEmpty(path))
                {
                    UnityEngine.Debug.LogError(
                        "<Wakapi> You don't have git installed. Disabling Version " +
                        "Control support for you. It can be re-enabled from the preferences."
                    );
                    EnableVersionControl = false;
                    return "master";
                }

                ProcessStartInfo processInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --abbrev-ref HEAD",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                var gitProcess = new Process();
                gitProcess.StartInfo = processInfo;

                string output = String.Empty;
                string error = String.Empty;
                try
                {
                    gitProcess.Start();
                    output = gitProcess.StandardOutput.ReadToEnd().Trim();
                    error = gitProcess.StandardError.ReadToEnd().Trim();
                    gitProcess.WaitForExit();
                }
                catch
                {
                    // silence is golden
                    // There shouldn't be any errors here since we are redirecting
                    // standard error
                    UnityEngine.Debug.LogError("<Wakapi> There was an error getting git branch.");
                }
                finally
                {
                    if (gitProcess != null)
                    {
                        gitProcess.Close();
                    }
                }

                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError(
                        "<Wakapi> There was an error getting your git branch. Disabling " +
                        "version control support. It can be re-enabled from the preferences.\n" +
                        error
                    );
                    EnableVersionControl = false;
                    return "master";
                }

                return output;
            }
        }

        #endregion
    }
}