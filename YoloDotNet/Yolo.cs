﻿using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using YoloDotNet.Data;
using YoloDotNet.Extensions;
using YoloDotNet.Models;

namespace YoloDotNet
{
    /// <summary>
    /// Detects objects in an image based on ONNX model.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the Yolo object detection model.
    /// </remarks>
    /// <param name="onnxModel">The path to the ONNX model.</param>
    /// <param name="cuda">Optional. Indicates whether to use CUDA for GPU acceleration (default is true).</param>
    /// <param name="gpuId">Optional. The GPU device ID to use when CUDA is enabled (default is 0).</param>
    public class Yolo(string onnxModel, bool cuda = true, int gpuId = 0) : YoloBase(onnxModel, cuda, gpuId)
    {
        /// <summary>
        /// Classifies a tensor abd returnbs a Classification list 
        /// </summary>
        /// <param name="tensor"></param>
        /// <param name="numberOfClasses"></param>
        /// <returns></returns>
        public override List<Classification> ClassifyImage(int numberOfClasses) => Tensors[OnnxModel.OutputNames[0]].Select((score, index) => new Classification
        {
            Confidence = score,
            Label = OnnxModel.Labels[index].Name
        })
            .OrderByDescending(x => x.Confidence)
            .Take(numberOfClasses)
            .ToList();

        /// <summary>
        /// Detects objects in a tensor and returns a ObjectDetection list.
        /// </summary>
        /// <param name="tensor">The input tensor containing object detection data.</param>
        /// <param name="image">The image associated with the tensor data.</param>
        /// <param name="threshold">The confidence threshold for accepting object detections.</param>
        /// <returns>A list of result models representing detected objects.</returns>
        public override List<ObjectResult> ObjectDetectImage(Image image, double threshold)
        {
            var result = new ConcurrentBag<ObjectResult>();

            var (w, h) = (image.Width, image.Height);

            var gain = Math.Max((float)w / OnnxModel.Input.Width, (float)h / OnnxModel.Input.Height);
            var ratio = Math.Min(OnnxModel.Input.Width / (float)image.Width, OnnxModel.Input.Height / (float)image.Height);
            var (xPad, yPad) = ((int)(OnnxModel.Input.Width - w * ratio) / 2, (int)(OnnxModel.Input.Height - h * ratio) / 2);

            var elements = OnnxModel.Labels.Length;
            var batchSize = OnnxModel.Outputs[0].BatchSize;
            var channels = OnnxModel.Outputs[0].Channels;

            var tensor = Tensors[OnnxModel.OutputNames[0]];

            for (var i = 0; i < batchSize; i++)
            {
                Parallel.For(0, channels, j =>
                {
                    // Calculate coordinates of the bounding box in the original image
                    var xMin = (int)((tensor[i, 0, j] - tensor[i, 2, j] / 2 - xPad) * gain);
                    var yMin = (int)((tensor[i, 1, j] - tensor[i, 3, j] / 2 - yPad) * gain);
                    var xMax = (int)((tensor[i, 0, j] + tensor[i, 2, j] / 2 - xPad) * gain);
                    var yMax = (int)((tensor[i, 1, j] + tensor[i, 3, j] / 2 - yPad) * gain);

                    // Keep bounding box coordinates within the image boundaries
                    xMin = Math.Clamp(xMin, 0, w);
                    yMin = Math.Clamp(yMin, 0, h);
                    xMax = Math.Clamp(xMax, 0, w);
                    yMax = Math.Clamp(yMax, 0, h);

                    for (int l = 0; l < elements; l++)
                    {
                        var confidence = tensor[i, 4 + l, j];

                        if (confidence < threshold) continue;

                        result.Add(new ObjectResult
                        {
                            Label = OnnxModel.Labels[l],
                            Confidence = confidence,
                            Rectangle = new Rectangle(xMin, yMin, xMax - xMin, yMax - yMin),
                            BoundingBoxIndex = j,
                        }); ;
                    }
                });
            }

            return RemoveOverlappingBoxes([.. result]);
        }

        /// <summary>
        /// Performs segmentation on the input image
        /// </summary>
        /// <param name="image">The input image for segmentation.</param>
        /// <param name="boundingBoxes">List of bounding boxes for segmentation.</param>
        /// <returns>List of Segmentation objects corresponding to the input bounding boxes.</returns>
        public override List<Segmentation> SegmentImage(Image image, List<ObjectResult> boundingBoxes)
        {
            var output = OnnxModel.Outputs[1]; // Segmentation output
            var tensor0 = Tensors[OnnxModel.OutputNames[0]];
            var tensor1 = Tensors[OnnxModel.OutputNames[1]];

            var elements = OnnxModel.Labels.Length + 4; // 4 = the boundingbox dimension (x, y, width, height)

            Parallel.ForEach(boundingBoxes, _parallelOptions, box =>
            //foreach (var box in boundingBoxes)
            {
                // Collect mask weights from the first tensor based on the bounding box index
                var maskWeights = Enumerable.Range(0, output.Channels)
                    .Select(i => tensor0[0, elements + i, box.BoundingBoxIndex])
                    .ToArray();

                // Create an empty image with the same size as the output shape
                using var segmentedImage = new Image<L8>(output.Width, output.Height);

                // Iterate over each empty pixel...
                for (int y = 0; y < output.Height; y++)
                    for (int x = 0; x < output.Width; x++)
                    {
                        // Iterate over each channel, calculate the pixel location (x, y) with its maskweight collected from first tensor.
                        var value = Enumerable.Range(0, output.Channels)
                                      .Sum(i => tensor1[0, i, y, x] * maskWeights[i]);

                        // Calculate and update the pixel luminance value
                        var pixelLuminance = CalculatePixelLuminance(Sigmoid(value));
                        segmentedImage[x, y] = new L8(pixelLuminance);
                    }

                segmentedImage.CropSegmentedArea(image, box.Rectangle);
                box.SegmentationMask = segmentedImage.GetPixels(p => CalculatePixelConfidence(p.PackedValue));
            });

            return boundingBoxes.Select(x => (Segmentation)x).ToList();
        }
    }
}