#if UNITY_IPHONE
using UnityEditor;
using UnityEngine.Assertions;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;
using System.IO;

public class PitayaBuildPostprocessor
{

  private static void CopyDirectory(string sourcePath, string destPath)
  {
    Assert.IsFalse(Directory.Exists(destPath));
    Directory.CreateDirectory(destPath);

    foreach (string file in Directory.GetFiles(sourcePath))
      File.Copy(file, Path.Combine(destPath, Path.GetFileName(file)));

    foreach (string dir in Directory.GetDirectories(sourcePath))
      CopyDirectory(dir, Path.Combine(destPath, Path.GetFileName(dir)));
  }

  private const string FRAMEWORK_PROJECT_ORIGIN_PATH =
    "Assets/Pitaya/Native/iOS"; // relative to project folder
  private const string FRAMEWORK_BUILD_ORIGIN_PATH =
    "Libraries/com.wildlifestudios.nuget.libpitaya/Native/iOS"; // relative to build folder
  private const string FRAMEWORK_TARGET_PATH =
    "Frameworks"; // relative to build folder

  private static string[] FRAMEWORK_NAMES = {"libpitaya.xcframework", "libcrypto.xcframework", "libssl.xcframework", "libuv.xcframework"};

  [PostProcessBuild]
  public static void OnPostprocessBuild(BuildTarget buildTarget, string path)
  {
    PBXProject proj = new PBXProject();
    string projPath = path + "/Unity-iPhone.xcodeproj/project.pbxproj";
    proj.ReadFromString(File.ReadAllText(projPath));

    string targetGuid = proj.GetUnityFrameworkTargetGuid();

    if (buildTarget == BuildTarget.iOS)
    {
      // Pitaya should be linked with zlib when on iOS.
      proj.AddBuildProperty(targetGuid, "OTHER_LDFLAGS", "-lz");
    }

    string originPath = FRAMEWORK_PROJECT_ORIGIN_PATH;
    if (!Directory.Exists(FRAMEWORK_PROJECT_ORIGIN_PATH)) {
      originPath = FRAMEWORK_BUILD_ORIGIN_PATH;
    }

    // Linking frameworks
    foreach(string framework in FRAMEWORK_NAMES){
      string sourcePath = Path.Combine(originPath, framework);
      string destPath = Path.Combine(FRAMEWORK_TARGET_PATH, framework);

      CopyDirectory(Path.Combine(path, sourcePath), Path.Combine(path, destPath));

      string fileGuid = proj.AddFile(destPath, destPath);
      proj.AddFileToEmbedFrameworks(targetGuid, fileGuid);
    }
    File.WriteAllText(projPath, proj.WriteToString());
  }

}
#endif
