using UnityEngine;
using System.Collections.Generic;
using System.Linq;

[RequireComponent(typeof(AudioSource))]
public class NPCAudioCapture : MonoBehaviour
{
    public float captureRadius = 45f;
    public float minVolume = 0.1f;
    public float GetLastAmplitude() => lastAmplitude;

    private List<float> audioBuffer = new List<float>();
    private int sampleRate;
    private bool isCapturing = false;
    private AudioSource[] cachedSources;
    private float lastAmplitude = 0f;

    void Start()
    {
        sampleRate = AudioSettings.outputSampleRate;
        cachedSources = FindObjectsOfType<AudioSource>();
    }

    public void StartCapture()
    {
        lock (audioBuffer)
            audioBuffer.Clear();
        isCapturing = true;
    }

    public byte[] StopAndGetWAV()
    {
        isCapturing = false;
        if (audioBuffer.Count == 0) return null;

        float[] samples;
        lock (audioBuffer)
        {
            samples = audioBuffer.ToArray();
        }
        AudioClip clip = AudioClip.Create("capture", samples.Length, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return WavUtility.FromAudioClip(clip);
    }

    private float[] mainThreadBuffer = new float[0];

    void Update()
    {
        if (!isCapturing) return;
        List<float> frame = new List<float>();
        foreach (var src in cachedSources)
        {
            if (src.gameObject == gameObject) continue;
            if (!src.isPlaying) continue;
            float dist = Vector3.Distance(transform.position, src.transform.position);
            if (dist > captureRadius) continue;
            float[] samples = new float[1024];
            src.GetOutputData(samples, 0);
            float vol = Mathf.Clamp01(1f - dist / captureRadius);
            for (int i = 0; i < samples.Length; i++)
                frame.Add(samples[i] * vol);
        }
        lock (audioBuffer)
            audioBuffer.AddRange(frame);

        if (frame.Count > 0)
            lastAmplitude = frame.Max(f => Mathf.Abs(f));
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        // keep empty - just needed for component
    }

    public bool HasAudibleSounds()
    {
        return true; // always capture, filter later
    }
}