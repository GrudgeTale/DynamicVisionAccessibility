using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TMPro;
using System.Collections;

/// <summary>
/// Manages universal dynamic accessibility screen adjustments (Post-Exposure, Contrast, Saturation)
/// using asynchronous GPU readbacks to prevent main thread CPU stalls.
/// </summary>
[AddComponentMenu("Accessibility/Dynamic Luminance Manager")]
public class DynamicLuminanceManager : MonoBehaviour
{
    public static DynamicLuminanceManager Instance { get; private set; }

    [Header("Post Processing Volume Configuration")]
    [Tooltip("The Global Volume containing the Color Adjustments override component.")]
    [SerializeField] private Volume globalVolume;
    private ColorAdjustments colorAdjustments;

    [Header("Manual Adjustment Fallbacks")]
    [Range(-2f, 2f)] public float manualBrightness = 0f;
    [Range(-100f, 100f)] public float manualContrast = 0f;
    [Range(-100f, 100f)] public float manualSaturation = 0f;

    [Header("Dynamic Adaptation Core Settings")]
    [Tooltip("If true, the system dynamically balances visual extremes based on screen content analytics.")]
    public bool dynamicMode = false;
    [Tooltip("The speed at which post-processing values shift to meet their target variations.")]
    [SerializeField] private float lerpSpeed = 3.0f; 
    [Tooltip("Multiplier regulating the intensity of the dynamic compensation curves.")]
    [Range(0f, 1f)] public float dynamicIntensity = 0.5f;

    [Header("Optional UI Elements Integration")]
    [Tooltip("Optional: Toggle element to switch between manual customization or automated balancing.")]
    public Toggle toggleDynamicMode;
    public Slider sliderBrightness;
    public Slider sliderContrast;
    public Slider sliderSaturation;
    public Slider sliderDynamicIntensity;

    [Header("Optional Label Outputs")]
    public TextMeshProUGUI textBrightness;
    public TextMeshProUGUI textContrast;
    public TextMeshProUGUI textSaturation;
    public TextMeshProUGUI textDynIntensity;

    [Header("Optimization Settings")]
    [Tooltip("The analysis cycle rate in fractions of a second. Lower values yield snappier results but consume more throughput.")]
    [SerializeField] private float analysisFrequency = 0.1f; 
    [Tooltip("Assign an isolated custom lower-res RenderTexture context to analyze, or leave blank to evaluate the native active Framebuffer context.")]
    [SerializeField] private RenderTexture sourceRenderTexture;

    // Runtime state variables
    private float averageLuminance = 0.5f;
    private float currentBrightness;
    private float currentContrast;
    private float currentSaturation;
    private bool isProcessingFrame = false;

    private void Awake()
    {
        // Thread-safe instance management
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        // Auto-assign validation checking
        if (globalVolume == null)
        {
            globalVolume = FindFirstObjectByType<Volume>();
        }

        if (globalVolume == null || !globalVolume.profile.TryGet(out colorAdjustments))
        {
            Debug.LogWarning("[DynamicLuminanceManager] Color Adjustments missing on associated Volume component profile. Internal logic deactivated.");
            enabled = false;
            return;
        }
    }

    private void Start()
    {
        LoadSettings();
        InitializeUIElements();
        RegisterUIEventBindings();
        UpdateSliderVisibilityStates(dynamicMode);
        
        StartCoroutine(ExecuteUniversalAnalysisLoop());
    }

    private void Update()
    {
        if (dynamicMode)
            CalculateDynamicTargetMatrix();
        else
            CalculateManualTargetMatrix();

        CommitAdjustmentsToVolume();
    }

    #region UI Integration Layer
    private void InitializeUIElements()
    {
        if (sliderBrightness) { sliderBrightness.value = manualBrightness; sliderBrightness.wholeNumbers = false; }
        if (sliderContrast) { sliderContrast.value = manualContrast; sliderContrast.wholeNumbers = true; }
        if (sliderSaturation) { sliderSaturation.value = manualSaturation; sliderSaturation.wholeNumbers = true; }
        if (toggleDynamicMode) toggleDynamicMode.isOn = dynamicMode;
        if (sliderDynamicIntensity) { sliderDynamicIntensity.value = dynamicIntensity; sliderDynamicIntensity.wholeNumbers = false; }

        RefreshVisualLabels();
    }

    private void RegisterUIEventBindings()
    {
        if (sliderBrightness) sliderBrightness.onValueChanged.AddListener(val => { manualBrightness = val; BindStringLabel(textBrightness, val, "F1"); SaveSettings(); });
        if (sliderContrast) sliderContrast.onValueChanged.AddListener(val => { manualContrast = val; BindStringLabel(textContrast, val, "F0"); SaveSettings(); });
        if (sliderSaturation) sliderSaturation.onValueChanged.AddListener(val => { manualSaturation = val; BindStringLabel(textSaturation, val, "F0"); SaveSettings(); });
        if (toggleDynamicMode) toggleDynamicMode.onValueChanged.AddListener(val => { dynamicMode = val; UpdateSliderVisibilityStates(val); SaveSettings(); });
        if (sliderDynamicIntensity) sliderDynamicIntensity.onValueChanged.AddListener(val => { dynamicIntensity = val; BindStringLabel(textDynIntensity, val, "F1"); SaveSettings(); });
    }

    private void BindStringLabel(TextMeshProUGUI element, float value, string standardFormat)
    {
        if (element != null) element.text = value.ToString(standardFormat);
    }

    private void RefreshVisualLabels()
    {
        BindStringLabel(textBrightness, manualBrightness, "F1");
        BindStringLabel(textContrast, manualContrast, "F0");
        BindStringLabel(textSaturation, manualSaturation, "F0");
        BindStringLabel(textDynIntensity, dynamicIntensity, "F1");
    }

    private void UpdateSliderVisibilityStates(bool isDynamic)
    {
        if (sliderBrightness) sliderBrightness.interactable = !isDynamic;
        if (sliderContrast) sliderContrast.interactable = !isDynamic;
        if (sliderSaturation) sliderSaturation.interactable = !isDynamic;
        if (sliderDynamicIntensity) sliderDynamicIntensity.interactable = isDynamic;
    }
    #endregion

    #region Async Content Analytics Core
    private IEnumerator ExecuteUniversalAnalysisLoop()
    {
        while (true)
        {
            if (dynamicMode && !isProcessingFrame)
            {
                yield return new WaitForEndOfFrame();
                
                RenderTexture targetContext = sourceRenderTexture != null ? sourceRenderTexture : RenderTexture.active;
                
                if (targetContext != null)
                {
                    isProcessingFrame = true;
                    AsyncGPUReadback.Request(targetContext, 0, TextureFormat.RGBA32, EvaluateGPUReadbackResult);
                }
            }
            yield return new WaitForSeconds(analysisFrequency);
        }
    }

    private void EvaluateGPUReadbackResult(AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            isProcessingFrame = false;
            return;
        }

        var processingBuffer = request.GetData<Color32>();
        if (processingBuffer.Length == 0) 
        {
            isProcessingFrame = false;
            return;
        }

        float absoluteLumaAccumulation = 0f;
        int standardStepOffset = Mathf.Max(1, processingBuffer.Length / 256); // Optimized downsampling logic
        int executionCounter = 0;

        for (int i = 0; i < processingBuffer.Length; i += standardStepOffset)
        {
            Color32 evaluationPixel = processingBuffer[i];
            
            float rawR = evaluationPixel.r / 255f;
            float rawG = evaluationPixel.g / 255f;
            float rawB = evaluationPixel.b / 255f;

            // Rec. 709 Standard Luminance Coefficients
            float localLuma = 0.2126f * rawR + 0.7152f * rawG + 0.0722f * rawB;

            // Flashlight / Flare Highlight Clamping Safeguard
            if (localLuma > 0.6f) localLuma = 0.4f;

            absoluteLumaAccumulation += localLuma;
            executionCounter++;
        }

        if (executionCounter > 0)
        {
            averageLuminance = absoluteLumaAccumulation / executionCounter;
        }

        isProcessingFrame = false;
    }
    #endregion

    #region Translation Processing & Calculations
    private void CalculateDynamicTargetMatrix()
    {
        float destinationB = 0f, destinationC = 0f, destinationS = 0f;
        const float lowBoundLuma = 0.4f; 
        const float highBoundLuma = 0.6f; 

        if (averageLuminance < lowBoundLuma)
        {
            float scalarFactor = (lowBoundLuma - averageLuminance) / lowBoundLuma;
            float exponentialWeight = Mathf.Pow(scalarFactor, 1.1f) * dynamicIntensity;

            destinationB = Mathf.Lerp(0f, 4.0f, exponentialWeight); 
            destinationC = Mathf.Lerp(0f, 100f, exponentialWeight); 
            destinationS = Mathf.Lerp(0f, 80f, exponentialWeight);
        }
        else if (averageLuminance > highBoundLuma)
        {
            float scalarFactor = (averageLuminance - highBoundLuma) / (1f - highBoundLuma);
            float exponentialWeight = Mathf.Pow(scalarFactor, 1.1f) * dynamicIntensity;
            
            destinationB = Mathf.Lerp(0f, -4.0f, exponentialWeight);
            destinationC = Mathf.Lerp(0f, -80f, exponentialWeight);
            destinationS = Mathf.Lerp(0f, -80f, exponentialWeight);
        }

        currentBrightness = Mathf.Lerp(currentBrightness, destinationB, Time.deltaTime * lerpSpeed);
        currentContrast = Mathf.Lerp(currentContrast, destinationC, Time.deltaTime * lerpSpeed);
        currentSaturation = Mathf.Lerp(currentSaturation, destinationS, Time.deltaTime * lerpSpeed);
    }

    private void CalculateManualTargetMatrix()
    {
        currentBrightness = Mathf.Lerp(currentBrightness, manualBrightness, Time.deltaTime * lerpSpeed);
        currentContrast = Mathf.Lerp(currentContrast, manualContrast, Time.deltaTime * lerpSpeed);
        currentSaturation = Mathf.Lerp(currentSaturation, manualSaturation, Time.deltaTime * lerpSpeed);
    }

    private void CommitAdjustmentsToVolume()
    {
        if (colorAdjustments == null) return;
        colorAdjustments.postExposure.value = currentBrightness;
        colorAdjustments.contrast.value = currentContrast;
        colorAdjustments.saturation.value = currentSaturation;
    }
    #endregion

    #region Persistent Storage Access Layer
    public void SaveSettings()
    {
        PlayerPrefs.SetFloat("Access_ManualBrightness", manualBrightness);
        PlayerPrefs.SetFloat("Access_ManualContrast", manualContrast);
        PlayerPrefs.SetFloat("Access_ManualSaturation", manualSaturation);
        PlayerPrefs.SetInt("Access_DynamicModeActive", dynamicMode ? 1 : 0);
        PlayerPrefs.SetFloat("Access_DynamicIntensityValue", dynamicIntensity);
        PlayerPrefs.Save();
    }

    public void LoadSettings()
    {
        manualBrightness = PlayerPrefs.GetFloat("Access_ManualBrightness", 0f);
        manualContrast = PlayerPrefs.GetFloat("Access_ManualContrast", 0f);
        manualSaturation = PlayerPrefs.GetFloat("Access_ManualSaturation", 0f);
        dynamicMode = PlayerPrefs.GetInt("Access_DynamicModeActive", 0) == 1;
        dynamicIntensity = PlayerPrefs.GetFloat("Access_DynamicIntensityValue", 0.5f);

        currentBrightness = manualBrightness;
        currentContrast = manualContrast;
        currentSaturation = manualSaturation;
    }
    #endregion
}