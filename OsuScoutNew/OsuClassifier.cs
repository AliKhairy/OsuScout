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
        private readonly List<InferenceSession> _ensembleModels = new();
        public ModelConfig Config { get; private set; }

        public void Initialize()
        {
            // 1. Load the Math Configuration
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "model_config.json");
            if (!File.Exists(configPath))
                throw new FileNotFoundException("Could not find model_config.json!");

            string json = File.ReadAllText(configPath);
            Config = JsonSerializer.Deserialize<ModelConfig>(json);

            // 2. Load the 5 Models into Memory Once
            for (int i = 1; i <= 5; i++)
            {
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", $"ensemble_model_{i}.onnx");
                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"Could not find {modelPath}!");

                // This takes a few milliseconds and zero Python resources
                _ensembleModels.Add(new InferenceSession(modelPath));
            }
        }

        public List<string> Predict(float[] rawFeatures)
        {
            if (rawFeatures.Length != 90)
                throw new ArgumentException($"Expected 90 features, but got {rawFeatures.Length}.");

            // 1. Apply the Scaler Math
            float[] scaledFeatures = new float[90];
            for (int i = 0; i < 90; i++)
            {
                scaledFeatures[i] = (rawFeatures[i] - Config.scaler_mean[i]) / Config.scaler_scale[i];
            }

            // Prepare the raw tensor without a name yet
            var inputTensor = new DenseTensor<float>(scaledFeatures, new[] { 1, 90 });
            float[] ensembleProbs = new float[Config.tags.Count];

            // 2. Run Inference across the Ensemble
            foreach (var model in _ensembleModels)
            {
                // THE FIX: Ask EACH model what its unique input name is
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
            List<string> finalTags = new List<string>();
            for (int i = 0; i < Config.tags.Count; i++)
            {
                float averageProb = ensembleProbs[i] / _ensembleModels.Count;

                if (averageProb >= 0.27f)
                {
                    finalTags.Add(Config.tags[i]);
                }
            }

            return finalTags;
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
    }

}