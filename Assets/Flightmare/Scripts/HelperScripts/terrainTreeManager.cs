using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Idea from: https://www.tutorialfor.com/questions-7293.htm
public class terrainTreeManager : MonoBehaviour
{
  void Awake()
  {
    // https://docs.unity3d.com/ScriptReference/Terrain.html
    Terrain terrain = Terrain.activeTerrain;
    float terrain_scale_x = terrain.terrainData.size.x;
    float terrain_scale_y = terrain.terrainData.size.y;
    float terrain_scale_z = terrain.terrainData.size.z;

    var trees = terrain.terrainData.treeInstances;

    // Instantiate all painted trees as actual prefabs with mesh collider.
    // TreeInstance: https://docs.unity3d.com/ScriptReference/TreeInstance.html
    foreach (var tree in trees)
    {
      GameObject obj = Instantiate(Resources.Load("Forest/" + terrain.terrainData.treePrototypes[tree.prototypeIndex].prefab.name)) as GameObject;
      obj.transform.position = new Vector3(tree.position.x * terrain_scale_x, tree.position.y * terrain_scale_y, tree.position.z * terrain_scale_z);
      obj.transform.localScale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);
      obj.transform.localRotation = Quaternion.Euler(0, 360 * tree.rotation / (2 * Mathf.PI), 0);

    }

    // disable drawing of tree and grass
    // terrain.drawTreesAndFoliage = false;

    // hack: set drawing distance of trees to zero
    terrain.treeDistance = 0;

  }
  // Start is called before the first frame update
  void Start()
  {

  }

  // Update is called once per frame
  void Update()
  {

  }
}
