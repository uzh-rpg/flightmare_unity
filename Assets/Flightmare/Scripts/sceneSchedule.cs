using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Dynamic scene management
using UnityEngine.SceneManagement;

namespace RPGFlightmare
{
  public class Scenes
  {
    public const string SCENE_INDUSTRIAL = "Environments/Industrial/Scenes/DemoScene";
    public const string SCENE_WAREHOUSE = "Environments/Warehouse/Scenes/DemoScene";
    public const string SCENE_NATUREFOREST = "NatureManufacture Assets/Forest Environment Dynamic Nature/Demo Scenes/Forest Demo Scene";
    public const string SCENE_WASTELAND = "ApocalypticWasteland/Scenes/Wasteland";
    //
    public List<string> scenes_list = new List<string>();
    public int default_scene_id;
    public int num_scene; // number of scenes in total
    public Scenes()
    {
      scenes_list.Add(SCENE_INDUSTRIAL);
      scenes_list.Add(SCENE_WAREHOUSE);
      scenes_list.Add(SCENE_NATUREFOREST);
      scenes_list.Add(SCENE_WASTELAND);

      default_scene_id = 0;
      num_scene = scenes_list.Count;
    }

  }
  public class sceneSchedule : MonoBehaviour
  {
    public GameObject camera_template; // Main camera
                                       // public GameObject cam_tmp;
    public Scenes scenes = new Scenes();
    public void Start()
    {
      loadScene(scenes.default_scene_id, true);
    }
    public void loadScene(int scene_id, bool camera_preview)
    {
      if (scene_id >= 0 && scene_id < scenes.num_scene)
      {
        SceneManager.LoadScene(scenes.scenes_list[scene_id]);
        scenes.default_scene_id = scene_id;
      }
      else
      {
        SceneManager.LoadScene(scenes.scenes_list[scenes.default_scene_id]);
      }
    }

    public void loadIndustrial()
    {
      loadScene(0, true);
    }

    public void loadWareHouse()
    {
      loadScene(1, true);
    }
    public void loadGarage()
    {
      loadScene(2, true);
    }

    public void loadForest()
    {
      loadScene(3, true);
    }

    public void loadTunnel()
    {
      loadScene(4, true);
    }



  }
}
