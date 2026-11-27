using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MakePlayerFixture
{
    const string ScenePath = "Assets/Fixture/FixtureScene.unity";

    static void PrepareScene()
    {
        Directory.CreateDirectory("Assets/Resources");
        if (!File.Exists("Assets/Resources/res_notes.txt"))
        {
            File.WriteAllText("Assets/Resources/res_notes.txt", "resource text asset\n");
            AssetDatabase.CopyAsset("Assets/Fixture/Textures/tex_rgba32.png", "Assets/Resources/res_tex.png");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Fixture/Prefabs/FixtureRoot.prefab");
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.transform.position = new Vector3(0, 1, 5);
        var extra = new GameObject("SceneOnly");
        extra.AddComponent<FixtureBehaviour>().text = "scene instance";
        var audio = extra.AddComponent<AudioSource>();
        audio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Fixture/Audio/sine_pcm.wav");
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    static void Build(ScriptingImplementation backend, string folder)
    {
        PrepareScene();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            target = BuildTarget.StandaloneWindows64,
            locationPathName = $"Out/{folder}/FixtureGame.exe",
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"FIXTURE_PLAYER {folder} {report.summary.result} {report.summary.totalSize} bytes errors={report.summary.totalErrors}");
    }

    public static void BuildMono() => Build(ScriptingImplementation.Mono2x, "Player-Mono");

    public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP, "Player-IL2CPP");
}
