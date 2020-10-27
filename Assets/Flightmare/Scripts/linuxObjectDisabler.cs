using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// disables baked reflection probes for Linux (not well supported)

public class linuxObjectDisabler : MonoBehaviour
{
  // start is called before the first frame update
  void Start()
  {
    // check runtime OS
    if (Application.platform == RuntimePlatform.LinuxPlayer){
      Debug.Log("Disabling baked reflection probes in linux.");

      // disable this object
      this.gameObject.SetActive(false);
    }
  }
}
