$ErrorActionPreference = 'Stop'
$modelFolder = Join-Path $PSScriptRoot 'models'
New-Item -ItemType Directory -Path $modelFolder -Force | Out-Null

$models = @(
    @{
        Name = 'watermark-detector.onnx'
        Url = 'https://huggingface.co/ayan4m1/Watermark-Detection-YOLO26-ONNX/resolve/c189f35eaf50974610647f58df94ecf94b890a90/onnx/model.onnx'
        Hash = '86c2009fadd599bcd66d6242e7a7d75052bb00c872af594ac6986816b0f6b270'
    },
    @{
        Name = 'lama_fp32.onnx'
        Url = 'https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx'
        Hash = '1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6'
    },
    @{
        Name = 'text-detector.onnx'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/28fe5895c24fd108c19eb3e8479f4ab385fbfc62/inference.onnx'
        Hash = 'd73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e'
    },
    @{
        Name = 'text-recognizer.onnx'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/b8f84f0b80c529de40b4fbb3544b84fa7233a513/inference.onnx'
        Hash = '5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634'
    }
)

foreach ($model in $models) {
    $path = Join-Path $modelFolder $model.Name
    if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path).Hash -eq $model.Hash) { continue }
    $partial = Join-Path $modelFolder ([Guid]::NewGuid().ToString('N') + '.download')
    try {
        Write-Host "Downloading $($model.Name)"
        curl.exe --fail --location --silent --show-error --retry 2 --max-time 300 --output $partial $model.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $($model.Name)." }
        if ((Get-FileHash -LiteralPath $partial).Hash -ne $model.Hash) { throw "Checksum mismatch for $($model.Name)." }
        Move-Item -LiteralPath $partial -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

Write-Host 'Local AI models are ready.'
