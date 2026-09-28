# Surface AI Studio

Offline photo editor for this Surface Laptop 7. Adjustments stay on the machine. The AI tools run on the Snapdragon X Elite Hexagon NPU through the Windows AI APIs.

## What it does

- Adjust brightness, contrast, saturation, warmth, fade, vignette, and sharpness, plus rotate, flip, and crop
- Upscale or sharpen with image super resolution
- Cut a subject out, blur the background, or replace it
- Click or drag to select an object, then cut it out or erase it
- Paint over something and erase it, or fill that area from a prompt
- Describe a photo, or read the text in it
- Generate a picture, restyle the current one, or make a coloring-book page

Image description, text recognition, and video super resolution are already on this PC. Super resolution, cutout, erase, object select, and image generation download the first time you use them, through Windows Update, and then run offline.

## Video

The app does not generate video. There is no text-to-video model that fits this laptop’s 16 GB of shared memory, and Windows does not ship one for the Hexagon NPU. Windows can sharpen an existing video of people (video super resolution). That model is installed. Making a clip from a prompt is not part of this version.

## Run

Developer Mode is required. From the repo:

```powershell
dotnet build src\SurfaceAIStudio\SurfaceAIStudio.csproj -c Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64
Add-AppxPackage -Register src\SurfaceAIStudio\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\AppxManifest.xml
Start-Process explorer.exe shell:AppsFolder\SurfaceAIStudio_tvwt0at5pdmdp!App
```

Open it from the Start menu. If the program file is started directly, it hands off to that installed app. Windows only allows the NPU models for the installed app.
