# DynamicVisionAccessibility
An open-source accessibility script created by Diño, Lumabi, and Saavedra for their thesis game, HueShift. It leverages asynchronous GPU readbacks to dynamically optimize exposure, contrast, and saturation in real time, maximizing on-screen clarity and object contrast for players with color vision deficiencies.



How it Works
Instead of altering the colors directly, this script acts like an automatic screen lighting equalizer. It constantly reads the game's screen brightness in the background without causing any game lag.

In Dark Areas: It automatically bumps up the game's brightness, contrast, and color vibrancy so objects and edges stand out clearly instead of getting lost in the shadows.

In Blinding Areas: It pulls back the exposure to prevent the screen from washing out into a blinding white glare.

This ensures that players with color vision deficiencies or low-light vision always have maximum visual clarity to tell shapes and objects apart.

The Loop 
[Camera Renders Frame] 
       │
[WaitForEndOfFrame] ➔ Wait until the GPU finishes drawing the final image
       │
[AsyncGPUReadback]  ➔ Send pixel data to CPU asynchronously (zero main-thread lag)
       │
[Downsample Matrix] ➔ Jump through the array using a calculated step size (~256 pixels)
       │
[Rec. 709 Formula]  ➔ Apply mathematical weights to Red, Green, and Blue values
       │
[Luma Clamping]     ➔ If an individual pixel > 0.6, force it to 0.4 (ignores flashlights)
       │
[Averaging]         ➔ Compute the final total average screen luminance
       │
[Lerped Adjustments]➔ Smoothly transition Exposure, Contrast, and Saturation via URP Volume



How to Setup
1. Setup the Post-Processing Volume
In your Unity Hierarchy, right-click and choose Volume > Global Volume.

Look at the Inspector for that Volume, and click the New button next to Profile.

Click Add Override, choose Post-processing > Color Adjustments, and check the boxes for Post-Exposure, Contrast, and Saturation (leave them at 0).

2. Add the Script
Save the code into a script named DynamicVisionAccessibility.cs.

Create an empty GameObject in your scene called AccessibilityManager.

Drag the script onto it.

Drag your Global Volume from the hierarchy into the Global Volume slot on the script.

Turn on Dynamic Mode in the inspector to let the script start automatically balancing your game's visibility!
