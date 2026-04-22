# WaterTool

A fully featured water rendering library for s&box. Featuring shader driven water surfaces with physically based buoyancy, waves simulation, and hull mesh exclusion.

---

## Features

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

## Minimal Setup

1. Configure the settings of the **WaterManager** in your scene (Project Settings > Systems > Water Manager)

### Standard (Used for pools, lakes and rivers)
2. Add a **WaterQuad** in your scene and configure the material

### Advanced (Used for large ocean/infinite water rendering)
2. Add a **WaterBodyRenderer** to render the surface
3. Add a **WaterBodyBaker** to the same gameobject as the **WaterBodyRenderer**
4. Press the **'Bake'** button to generate water bodies all around your terrain