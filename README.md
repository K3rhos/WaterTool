# 🌊 WaterTool

A fully featured water rendering library for s&box. Featuring shader driven water surfaces with physically based buoyancy, waves simulation, and hull mesh exclusion.

---

## 📷 Preview

https://github.com/user-attachments/assets/6c5543ee-a977-4cab-8a07-16b7ef06c1a8

<img width="2176" height="1224" alt="BakedWaterBodies" src="https://github.com/user-attachments/assets/17a9f003-4117-4ad4-bf9d-627a0b26c998" />
<img width="2176" height="1224" alt="TestPools" src="https://github.com/user-attachments/assets/dda38da7-087f-4dcf-892b-d684f9cace71" />
<img width="2176" height="1224" alt="TestLake" src="https://github.com/user-attachments/assets/8dcd11ab-4894-43ed-8b74-e1d0bd592d36" />
<img width="2176" height="1224" alt="Ocean" src="https://github.com/user-attachments/assets/2f91426e-355d-46dc-a1b2-6af36512825b" />

## 📗 Features

### Rendering
- **Clipmap water surface** Compute shader driven mesh that tiles out from the camera for infinite looking water with LODs
- **WaterDefinition profiles** Asset based wave configuration for Ocean, Lake, River, Pool, or custom types. Controls detail waves and swell waves independently (intensity, speed, scale, direction, octaves, lacunarity, persistence, steepness)
- **Multi-octave Gerstner waves** Evaluates wave displacement and velocity at any world position for use in gameplay and physics

### Physics & Buoyancy
- **Buoyancy component** 9-point hull sampling with spring/damping forces, drag, angular drag, and horizontal wave transport
- **Air volume leaking** Buoyancy degrades as an object becomes fully submerged, simulating flooding if needed

### Exclusion Volumes
- **WaterExclusionVolume** OBB based volume that suppresses water surface rendering inside it (for submarine interior, underwater bases, etc.)
- **HullWaterExclusionVolume** When placed on the same GameObject as a `ModelRenderer` it will extracts the physics collision mesh, uploads the triangle list to the GPU, and performs a per-pixel point-in-mesh test using the Möller–Trumbore ray intersection algorithm. It will correctly excludes the entire interior of any convex or concave hull (Really useful for a boat interior)

### Post-Processing
- **SimpleFog** Lightweight atmospheric fog with configurable color, intensity, and opacity
- **WobbleEffect** Screen space ripple/distortion with configurable frequency, amplitude, and speed

---

## 📀 Minimal Setup

1. Configure the settings of the **WaterManager** in your scene (Project Settings > Systems > Water Manager)

### Standard (Used for pools, lakes and rivers)
2. Add a **WaterQuad** in your scene and configure the material

### Advanced (Used for large ocean/infinite water rendering)
2. Add a **WaterBodyRenderer** to render the surface
3. Add a **WaterBodyBaker** to the same gameobject as the **WaterBodyRenderer**
4. Press the **'Bake'** button to generate water bodies all around your terrain
