using System.Collections.Generic;

using UnityEngine;

public class FrameTimeAverage : MonoBehaviour
{
    public TextMesh textMesh;

    private List<float> frameTimeDeltas;

    private void Awake()
    {
        frameTimeDeltas = new List<float>();
    }

    private void Update()
    {
        frameTimeDeltas.Add(Time.deltaTime);

        double averageFrameTime = 0;

        for(int i = 0; i < frameTimeDeltas.Count; i++)
        {
            averageFrameTime += frameTimeDeltas[i];
        }

        averageFrameTime /= frameTimeDeltas.Count;

        textMesh.text = string.Format("{0} avg fps\n{1} avg ms", 1.0 / averageFrameTime, averageFrameTime);
    }
}
