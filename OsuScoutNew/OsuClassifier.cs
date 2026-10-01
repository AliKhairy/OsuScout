using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;


namespace OsuScoutNew
{
    public class OsuClassifier : IDisposable
    {
        // Used only when model_config.json predates these fields. The training
        // repo's mlops/labels.py is the source of truth, and exports them.
        private const double DefaultThreshold = 0.26;
        private const int DefaultDisplayDecimals = 2;

        private readonly List<InferenceSession> _ensembleModels = new();
        public ModelConfig Config { get; private set; }

        // Fingerprint of the model in use: a hash of model_config.json, whose scaler
        // constants are unique to each training run and which also carries the
        // threshold and redundancy rules. Tags stored in the library were produced
        // by one of these; when it changes, they are stale and get re-computed.
        public string ModelId { get; private set; }

        // assetsDir defaults to the app's Assets folder; the parity harness points
        // it at an exported candidate instead.
        public void Initialize(string assetsDir = null)
        {
            assetsDir ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

            // 1. Load the Math Configuration
            string configPath = Path.Combine(assetsDir, "model_config.json");
            if (!File.Exists(configPath))
                throw new FileNotFoundException("Could not find model_config.json!");

            string json = File.ReadAllText(configPath);
            Config = JsonSerializer.Deserialize<ModelConfig>(json);
            ModelId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(configPath)));

            // 2. Load the 5 Models into Memory Once
            for (int i = 1; i <= 5; i++)
            {
                string modelPath = Path.Combine(assetsDir, $"ensemble_model_{i}.onnx");
                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"Could not find {modelPath}!");

                // This takes a few milliseconds and zero Python resources
                _ensembleModels.Add(new InferenceSession(modelPath));
            }
        }

        // The ensemble's averaged probability for every tag, in Config.tags order.
        public float[] PredictProbabilities(float[] rawFeatures)
        {
            // Feature count is driven by the trained model's scaler config, not a magic
            // number. Retrain with more/fewer features -> regenerate model_config.json and
            // this adapts automatically (FeatureExtractor must still produce the same count).
            int featureCount = Config.scaler_mean.Count;

            if (rawFeatures.Length != featureCount)
                throw new ArgumentException($"Expected {featureCount} features, but got {rawFeatures.Length}.");

            // 1. Apply the Scaler Math
            float[] scaledFeatures = new float[featureCount];
            for (int i = 0; i < featureCount; i++)
            {
                scaledFeatures[i] = (rawFeatures[i] - Config.scaler_mean[i]) / Config.scaler_scale[i];
            }

            // Prepare the raw tensor without a name yet
            var inputTensor = new DenseTensor<float>(scaledFeatures, new[] { 1, featureCount });
            float[] ensembleProbs = new float[Config.tags.Count];

            // 2. Run Inference across the Ensemble
            foreach (var model in _ensembleModels)
            {
                // Ask EACH model what its unique input name is
                string modelInputName = model.InputMetadata.Keys.First();

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(modelInputName, inputTensor)
                };

                using var results = model.Run(inputs);

                var outputTensor = results.First().AsTensor<float>();

                for (int i = 0; i < Config.tags.Count; i++)
                {
                    ensembleProbs[i] += outputTensor[0, i];
                }
            }

            // 3. Average the probabilities
            for (int i = 0; i < Config.tags.Count; i++)
                ensembleProbs[i] /= _ensembleModels.Count;
            return ensembleProbs;
        }

        public List<string> Predict(float[] rawFeatures)
        {
            float[] probs = PredictProbabilities(rawFeatures);

            // A tag counts when its probability, shown to two decimals, reaches the
            // threshold: one displayed as 0.26 is predicted at 0.26. Compared in
            // float, as numpy compares the float32 ensemble output in training.
            double threshold = Config.threshold ?? DefaultThreshold;
            int decimals = Config.display_decimals ?? DefaultDisplayDecimals;
            float cutoff = (float)(threshold - 0.5 * Math.Pow(10, -decimals));

            var tags = new List<string>();
            for (int i = 0; i < Config.tags.Count; i++)
            {
                if (probs[i] >= cutoff)
                    tags.Add(Config.tags[i]);
            }
            return SuppressRedundant(tags);
        }

        // Hide a general tag next to a more specific one that already says it
        // (jumps beside large jumps). The rules ship in model_config.json.
        private List<string> SuppressRedundant(List<string> tags)
        {
            if (Config.suppressed_by == null || Config.suppressed_by.Count == 0) return tags;
            var shown = new HashSet<string>(tags);
            return tags.Where(t => !(Config.suppressed_by.TryGetValue(t, out var by) && by.Any(shown.Contains))).ToList();
        }

        public void Dispose()
        {
            // Clean up memory when the app closes
            foreach (var model in _ensembleModels)
            {
                model.Dispose();
            }
        }
    }

    // Helper class to read your JSON
    public class ModelConfig
    {
        public List<float> scaler_mean { get; set; }
        public List<float> scaler_scale { get; set; }
        public List<string> tags { get; set; }

        // Written by the training repo's extract_config.py. Absent in configs from
        // before v2, which are v1-feature models.
        public int? feature_version { get; set; }
        public double? threshold { get; set; }
        public int? display_decimals { get; set; }
        public Dictionary<string, List<string>> suppressed_by { get; set; }

        public int FeatureVersion => feature_version ?? 1;
    }

}
