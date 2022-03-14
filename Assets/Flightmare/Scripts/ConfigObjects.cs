
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Globalization;
using MessageSpec;

namespace RPGFlightmare
{
  // Keeps track of gameobjects and their initialization and instantiation.
  public class StaticObject 
  {
    // Constructor
    private Dictionary<string, ObjectState>  objects;

    // Get Wrapper object, defaulting to a passed in template if it does not exist.
    public ObjectState  getWrapperObject(string ID, GameObject template)
    {
      if (!objects.ContainsKey(ID))
      {
        Debug.Log("Creating new object.");
        // Create and save object from template
        objects[ID] = new ObjectState(template);
      }
      return objects[ID];
    }

    public GameObject getGameobject(string ID, GameObject template)
    {
      return getWrapperObject(ID, template).gameObj;
    }

    public StaticObject()
    {
      objects = new Dictionary<string, ObjectState>() { };
    }

  }

  public class ObjectState
  {
    public bool initialized { get; set; } = false;
    public GameObject gameObj { get; set; }
    public GameObject template { get; set; }
    // public PostProcessingProfile postProcessingProfile { get; set; }
    // Constructor

    public ObjectState(GameObject template)
    {
      this.gameObj = GameObject.Instantiate(template);
      this.template = template;
    }
  }

  public class ConfigObjects : MonoBehaviour
  {

    // private StaticObject static_object;
    // Start is called before the first frame update
    void Start()
    {
      // DontDestroyOnLoad(this.gameObject);
      // static_object = new StaticObject();
      // ReadCSVFile();
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void ReadCSVFile(UnityState_t internal_state, string object_csv)
    {
      Debug.Log("Load CSV Files");
      // const string csv_fle ="./Assets/static_obstacles.csv";
      StreamReader str_reader = new StreamReader(object_csv);
      bool end_of_file = false;
      int obj_id = 0;

      while(!end_of_file) {
        string data_string = str_reader.ReadLine();
        if (data_string == null) {
          end_of_file = true;
          break;
        }

        obj_id += 1;
        if (obj_id <= 1) {
          continue;
        }

        var data_values = data_string.Split(",");
        Debug.Log("data " + data_values[1].ToString() + obj_id.ToString());

        string obj_name= "static_object_" + obj_id.ToString();
        string prefab_id = data_values[0].ToString();
        GameObject prefab = Resources.Load(prefab_id) as GameObject;
        GameObject obj = internal_state.getGameobject(obj_name, prefab);
        // 

        Vector3 position ;
        Quaternion rotation;
        Vector3 size;
        // 
        position.x = float.Parse(data_values[1], CultureInfo.InvariantCulture.NumberFormat);
        position.y = float.Parse(data_values[3], CultureInfo.InvariantCulture.NumberFormat);
        position.z = float.Parse(data_values[2], CultureInfo.InvariantCulture.NumberFormat);

        // 
        rotation.x = float.Parse(data_values[5], CultureInfo.InvariantCulture.NumberFormat);
        rotation.y = float.Parse(data_values[6], CultureInfo.InvariantCulture.NumberFormat);
        rotation.z = float.Parse(data_values[7], CultureInfo.InvariantCulture.NumberFormat);
        rotation.w = float.Parse(data_values[4], CultureInfo.InvariantCulture.NumberFormat);
        // 
        size.x = float.Parse(data_values[8], CultureInfo.InvariantCulture.NumberFormat);
        size.y = float.Parse(data_values[9], CultureInfo.InvariantCulture.NumberFormat);
        size.z = float.Parse(data_values[10], CultureInfo.InvariantCulture.NumberFormat);

        obj.transform.SetPositionAndRotation(position, rotation);
        obj.transform.localScale = size;
      }

    }


  }
}
