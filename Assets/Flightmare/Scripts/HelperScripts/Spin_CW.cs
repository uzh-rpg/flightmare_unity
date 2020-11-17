using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Spin_CW : MonoBehaviour
{
  public float spinSpeed = -2000.0f;
  // Start is called before the first frame update
  void Start()
  {

  }

  // Update is called once per frame
  void Update()
  {
    transform.Rotate(0, 0, spinSpeed * Time.deltaTime);
  }
}
