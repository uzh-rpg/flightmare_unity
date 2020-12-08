using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class collisionHandler : MonoBehaviour
{
  public bool hasCollided = false;
  List<string> ignore = new List<string>(); // object that should not appear in pointcloud
  Collider this_coll;
  
  //Start is called before the first frame update
  void Start() {
      //Unwanted objects for collision detection
      ignore.Add("HDCamera");
      ignore.Add("HDCamera(Clone)");
      ignore.Add("Transparent_Cube");

      this_coll = this.gameObject.GetComponent<Collider>();

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
