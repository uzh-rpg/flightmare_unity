using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class collisionHandler : MonoBehaviour
{
  public bool hasCollided = false;
  
  // Start is called before the first frame update
  // void Start() {}

  // Update is called once per frame
  // void Update() {}

  private void OnTriggerEnter(Collider collision)
  {
      hasCollided = true;
  }

  private void OnTriggerStay(Collider collision)
  {
      hasCollided = true;
  }

  private void OnTriggerExit(Collider collision)
  {
      hasCollided = false;
  }
}
