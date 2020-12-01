using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class collisionHandler : MonoBehaviour
{
  public bool hasCollided = false;
  List<string> ignore = new List<string>(); // object that should not appear in pointcloud
  
  //Start is called before the first frame update
  void Start() {
      //Unwanted objects for collision detection
      ignore.Add("HDCamera");
      ignore.Add("Drone_red");
      ignore.Add("HDCamera(Clone)");
      ignore.Add("Drone_red(Clone)");
      ignore.Add("Transparent_Cube");

      // get all colliders and change them to trigger
       var colls = FindObjectsOfType<Collider>();
       foreach (var coll in colls)
       {
           coll.isTrigger = true;
       }

  }

  //Update is called once per frame
  void Update() {}

  private void OnTriggerEnter(Collider other)
  {
    if (!ignore.Contains(other.gameObject.name))
    {
        hasCollided = true;
    }
  }

  private void OnTriggerStay(Collider other)
  {
    if (!ignore.Contains(other.gameObject.name))
    {
        hasCollided = true;
    }
  }

  private void OnTriggerExit(Collider other)
  {
    if (!ignore.Contains(other.gameObject.name))
    {
        hasCollided = false;
    }
  }
}
