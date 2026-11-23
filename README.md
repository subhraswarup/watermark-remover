# Watermark Remover

A Windows desktop editor for removing marks from still images. It processes images locally using the pixels around your selection.

## Build and run on Windows

Install Git for Windows and the .NET 10 SDK. Clone this repository using the URL under **Code** on GitHub, then open PowerShell in the folder that contains `WatermarkRemover.csproj`.

To download the local AI models, build the app, and open it, run these commands in order:

```powershell
dotnet --version
.\Setup-AiModels.ps1
dotnet build -c Release
dotnet .\bin\Release\net10.0-windows\WatermarkRemover.dll
```

The model setup needs internet access and verifies the downloaded files. The build also needs access to NuGet on the first restore. Once those files are in place, image processing runs locally. To reopen the same build, run the last command again. After pulling source updates, rebuild before launching.

```powershell
git pull --ff-only
.\Setup-AiModels.ps1
dotnet build -c Release
dotnet .\bin\Release\net10.0-windows\WatermarkRemover.dll
```

## Edit an image

1. Click **Choose image** or drop a PNG, JPEG, or BMP image up to 16 megapixels onto the window.
2. Choose **AI auto-remove** to detect visible watermarks and preview a repair. To mark a watermark yourself, use **Brush** and **Erase**, then choose **Remove watermark**. Scroll to zoom and right-drag to pan.
3. Compare **Original** and **Result** before saving. Use **Undo** or `Ctrl+Z` to reverse a stroke, clear, or removal. Undo history is kept for the open image.
4. Choose **Save copy** to export a PNG or JPEG. PNG preserves transparency. JPEG gives transparent pixels a white background.

## Process a batch

1. Click **Batch** and add or drop up to 30 images. You can also drop several files onto the main window to queue them.
2. Choose an output folder and click **Run batch**. The app processes the queue in order and automatically saves successful repairs as `-cleaned.png` copies. If a name is taken, the app adds a number.
3. Check the result beside each file. Images with no confident detection are skipped. A failed file does not interrupt the rest of the queue. Choose **Stop after current** to finish the active image and leave the remaining files queued.

## Set up local AI

The setup script downloads about 250 MB of model files. The app uses a [YOLO26 watermark detector](https://huggingface.co/ayan4m1/Watermark-Detection-YOLO26-ONNX), [PP-OCR text detection](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx) and [recognition](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx), and [LaMa inpainting](https://huggingface.co/Carve/LaMa-ONNX) through ONNX Runtime on the CPU. Inference runs offline. Automatic detection can miss faint or unfamiliar marks. Brush over those marks to give LaMa an exact selection.

Without the model files, manual removal uses interpolation. Small marks over smooth backgrounds work best in that mode.

Automatic removal matches supported Gemini and Grok corner logos and recognizes common text watermark phrases such as “Demo Image,” “Sample Image,” and “Preview Only.” It repairs recognized text in sections at the original image size. A Gemini match reverses the translucent overlay locally. A Grok match uses its outline as a repair mask. The [GeminiWatermarkTool reference masks](https://github.com/allenk/GeminiWatermarkTool/blob/7c6a99f2c3df97eb3c430ef87a0c962aea5cb80e/assets/embedded_assets.hpp) are distributed with their [MIT license](Assets/Gemini/LICENSE.txt).
