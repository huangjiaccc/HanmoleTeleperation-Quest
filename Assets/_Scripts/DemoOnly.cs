using UnityEngine;
using UnityEngine.UI;


public class DemoOnly : MonoBehaviour
{
    [SerializeField]
    private Button btn_immersive;
    [SerializeField]
    private Button btn_mask;
    [SerializeField]
    private Button btn_stereo;
    [SerializeField]
    private RawImage videoImg;
    [SerializeField]
    private MeshRenderer meshRenderer;

    private bool isImmersive = false;
    private bool isMaskVideo = false;
    private bool isStereo = false;


    void Start()
    {
        btn_immersive.onClick.AddListener(() => { ImmersiveMode(); });
        btn_stereo.onClick.AddListener(() => { isStereo = !isStereo; UseStereo(); });
        btn_mask.onClick.AddListener(() =>
        {
            isMaskVideo = !isMaskVideo;
            UseMaskToVideo();
        });
    }


    void ImmersiveMode()
    {
        isImmersive = !isImmersive;
        if (isImmersive)
        {
            Camera.main.clearFlags = CameraClearFlags.Skybox;
        }
        else
        {
            Camera.main.backgroundColor = new Color(0, 0, 0, 0);
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
        }
    }

    void UseMaskToVideo()
    {
        if (isMaskVideo)
        {
            videoImg.material.SetInt("_UseMask", 1);
            if(meshRenderer!= null)
            {
                meshRenderer.material.SetInt("_UseMask", 1);
            }
        }
        else
        {
            videoImg.material.SetInt("_UseMask", 0);
            if(meshRenderer!= null)
            {
                meshRenderer.material.SetInt("_UseMask", 0);
            }
        }
    }

    void UseStereo()
    {
        if (isStereo)
        {
            videoImg.material.SetInt("_UseStereo", 1);
            if(meshRenderer!= null)
            {
                meshRenderer.material.SetInt("_UseStereo", 1);
            }
        }
        else
        {
            videoImg.material.SetInt("_UseStereo", 0);
            if(meshRenderer!= null)
            {
                meshRenderer.material.SetInt("_UseStereo", 0);
            }
        }
    }
}
