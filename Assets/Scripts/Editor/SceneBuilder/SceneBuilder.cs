using UnityEditor;

namespace Pascension.Editor
{
    /// <summary>
    /// Menu entry for the code-built scenes. All construction logic lives in
    /// Pascension.Game.EditorSupport.SceneConstruction (editor-only compiled) because
    /// this Editor assembly does not reference the uGUI/TMP/InputSystem assemblies.
    /// </summary>
    public static class SceneBuilder
    {
        [MenuItem("Pascension/Setup/Build All Scenes")]
        public static void BuildAllScenes() =>
            Game.EditorSupport.SceneConstruction.BuildAllScenes();
    }
}
