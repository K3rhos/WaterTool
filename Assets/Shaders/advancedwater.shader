
HEADER
{
	Description = "Advanced Water Shader";
}

FEATURES
{
	#include "common/features.hlsl"

    Feature(F_TRANSLUCENT, 0..1, "Rendering");
}

MODES
{
	Forward();
	Depth();
	ToolsShadingComplexity("tools_shading_complexity.shader");
}

COMMON
{
	#include "common/shared.hlsl"
	#include "procedural.hlsl"

	#define S_UV2 1
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic(Color); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float3 vPositionOs : TEXCOORD14;
	float3 vNormalOs : TEXCOORD15;
	float4 vTangentUOs_flTangentVSign : TANGENT	< Semantic(TangentU_SignV); >;
	float4 vColor : COLOR0;
	float4 vTintColor : COLOR1;
	#if (PROGRAM == VFX_PROGRAM_PS)
		bool vFrontFacing : SV_IsFrontFace;
	#endif
};



VS
{
	#include "common/vertex.hlsl"

	// Detail waves
	float g_flWavesIntensity < Attribute("WavesIntensity"); Default1(4); >;
	float g_flWavesSpeed < Attribute("WavesSpeed"); Default1(0.3); >;
	float g_flWavesScale < Attribute("WavesScale"); Default1(0.05); >;
	float2 g_vWavesDirection < Attribute("WavesDirection"); Default2(1, 0.5); >;
	int g_nWavesOctaves < Attribute("WavesOctaves"); Default1(3); >;
	float g_flWavesLacunarity < Attribute("WavesLacunarity"); Default1(2.0); >;
	float g_flWavesPersistence < Attribute("WavesPersistence"); Default1(0.5); >;
	float g_flWavesSteepness < Attribute("WavesSteepness"); Default1(0.5); >;

	// Swell waves
	float g_flSwellIntensity < Attribute("SwellIntensity"); Default1(15); >;
	float g_flSwellSpeed < Attribute("SwellSpeed"); Default1(0.1); >;
	float g_flSwellScale < Attribute("SwellScale"); Default1(0.002); >;
	float2 g_vSwellDirection < Attribute("SwellDirection"); Default2(0.7, 0.3); >;
	int g_nSwellOctaves < Attribute("SwellOctaves"); Default1(2); >;
	float g_flSwellLacunarity < Attribute("SwellLacunarity"); Default1(1.8); >;
	float g_flSwellPersistence < Attribute("SwellPersistence"); Default1(0.6); >;
	float g_flSwellSteepness < Attribute("SwellSteepness"); Default1(0.3); >;

	float g_flWaterTime < Attribute("WaterTime"); Default1(0); >;

	// Gerstner wave sum (Identical formula to C# ComputeGerstner for exact CPU/GPU match.)
	// Returns float3: (dx, dy, dz) displacement. XY = horizontal orbital motion, Z = vertical.
	float3 ComputeGerstner(float2 worldXY, float scale, float speed, float2 dir, int octaves, float lacunarity, float persistence, float steepness, float time)
	{
		float2 wDir = normalize(dir);
		float t = time * speed;

		float3 displacement = float3(0, 0, 0);
		float amp = 1.0;
		float freq = scale;
		float maxAmp = 0;

		for (int oct = 0; oct < octaves; oct++)
		{
			float angle = oct * 1.2;
			float2 octDir = float2(
				wDir.x * cos(angle) - wDir.y * sin(angle),
				wDir.x * sin(angle) + wDir.y * cos(angle)
			);

			float phase = freq * dot(octDir, worldXY) + t * freq * 0.5;
			displacement.xy += steepness * amp * octDir * cos(phase);
			displacement.z  += amp * sin(phase);

			maxAmp += amp;
			amp *= persistence;
			freq *= lacunarity;
		}

		return displacement / maxAmp;
	}

	PixelInput MainVs(VertexInput v)
	{
		PixelInput i = ProcessVertex(v);
		i.vPositionOs = v.vPositionOs.xyz;
		i.vColor = v.vColor;

		ExtraShaderData_t extraShaderData = GetExtraPerInstanceShaderData(v.nInstanceTransformID);
		i.vTintColor = extraShaderData.vTint;

		VS_DecodeObjectSpaceNormalAndTangent(v, i.vNormalOs, i.vTangentUOs_flTangentVSign);

		// Use the original grid position for wave evaluation
		float2 worldXY = i.vPositionWs.xy;

        // Detail waves
        float3 detailDisp = ComputeGerstner(worldXY, g_flWavesScale, g_flWavesSpeed, g_vWavesDirection, g_nWavesOctaves, g_flWavesLacunarity, g_flWavesPersistence, g_flWavesSteepness, g_flWaterTime) * g_flWavesIntensity;

        // Swell waves
        float3 swellDisp = ComputeGerstner(worldXY, g_flSwellScale, g_flSwellSpeed, g_vSwellDirection, g_nSwellOctaves, g_flSwellLacunarity, g_flSwellPersistence, g_flSwellSteepness, g_flWaterTime) * g_flSwellIntensity;

		// Apply full XY + Z displacement (XY creates the orbital Gerstner motion)
		i.vPositionWs.xyz += detailDisp + swellDisp;
		i.vPositionPs.xyzw = Position3WsToPs(i.vPositionWs.xyz);

		return FinalizeVertex(i);
	}
}



PS
{
	#include "common/pixel.hlsl"
	#include "water_inclusion_volume.fxc"
	#include "water_exclusion_volume.fxc"
	#include "water_hull_exclusion.fxc"

    StaticCombo(S_TRANSLUCENT, F_TRANSLUCENT, Sys(ALL));
	
	RenderState(CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT);

    // We're already feeding the FrameBufferCopyTexture yourself, not needed
    // BoolAttribute(bWantsFBCopyTexture, true);

	// ── Textures ──
    Texture2D g_tFrameBufferCopyTexture < Attribute("FrameBufferCopyTexture"); SrgbRead(false); > ;

	CreateInputTexture2D( MainNormal, Linear, 8, "NormalizeNormals", "_normal", "Normals,0/,0/0", DefaultFile( "materials/dev/white_color.tga" ) );
	CreateInputTexture2D( SecondNormal, Linear, 8, "NormalizeNormals", "_normal", "Normals,0/,0/0", DefaultFile( "materials/dev/white_color.tga" ) );
	Texture2D g_tMainNormal < Channel( RGBA, Box( MainNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;
	Texture2D g_tSecondNormal < Channel( RGBA, Box( SecondNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;
	TextureAttribute( LightSim_DiffuseAlbedoTexture, g_tSecondNormal )
	TextureAttribute( RepresentativeTexture, g_tSecondNormal )

	// ── Normal Scrolling ──
	float2 g_vNormalTiling < Attribute( "NormalTiling" ); Default2( 50,50 ); >;
	float g_flSpeedMainNormal < UiGroup( "Normals,0/,0/0" ); Default1( 50 ); Range1( -1000, 1000 ); >;
	float g_flSpeedSecondNormal < UiGroup( "Normals,0/,0/0" ); Default1( -25 ); Range1( -1000, 1000 ); >;
	float g_flNormalStrength < UiType( Slider ); UiGroup( "Normals,0/,0/0" ); Default1( 1 ); Range1( 0, 2 ); >;

	// ── Refraction ──
	float g_flRefractionStrength < UiType( Slider ); UiGroup( "Refraction,0/,0/0" ); Default1( 0.1 ); Range1( 0, 1 ); >;

	// ── Depth Coloring ──
	float4 g_vShallowColor < UiType( Color ); UiGroup( "Depth,0/,0/0" ); Default4( 0.37, 1.00, 0.89, 0.57 ); >;
    float4 g_vDeepColor < UiType(Color); UiGroup("Depth,0/,0/0"); Default4(0.00, 0.88, 1.00, 0.48); > ;
    float g_flDepthMax < Attribute("DepthMax"); Default1(1000); > ;
    float g_flDepthMultiplier < UiGroup("Depth,0/,0/0"); Default1(1); Range1(0, 2); > ;
	float g_flDepthFalloff < UiGroup( "Depth,0/,0/0" ); Default1( 0.5 ); Range1( 0, 2 ); >;
	float g_flDepthBlend < UiGroup( "Depth,0/,0/0" ); Default1( 0.75 ); Range1( 0, 1 ); >;
	float g_flShoreOpacity < UiType( Slider ); UiGroup( "Depth,0/,0/0" ); Default1( 0 ); Range1( 0, 1 ); >;
	float g_flShoreOpacityRange < UiGroup( "Depth,0/,0/0" ); Default1( 100 ); Range1( 0, 1000 ); >;

	// ── Surface Caustics ──
	float g_flCausticsThresholdMin < UiGroup( "Caustics,0/,0/0" ); Default1( 0.3 ); Range1( 0, 2 ); >;
	float g_flCausticsThresholdMax < UiGroup( "Caustics,0/,0/0" ); Default1( 2 ); Range1( 0, 2 ); >;
	float g_flCausticsTilingMultiplier < UiGroup( "Caustics,0/,0/0" ); Default1( 1 ); Range1( 0.1, 10 ); >;
	float g_flCausticsScrollSpeed < UiGroup( "Caustics,0/,0/0" ); Default1( 0.01 ); Range1( 0, 0.1 ); >;
	float g_flCausticsAnimSpeed < UiGroup( "Caustics,0/,0/0" ); Default1( 1 ); Range1( 0.1, 10 ); >;
	float g_flCausticsIntensity < UiGroup( "Caustics,0/,0/0" ); Default1( 0 ); Range1( 0, 1 ); >;

	// ── Edge Foam ──
	float4 g_vFoamColor < UiType( Color ); UiGroup( "Foam,0/,0/0" ); Default4( 1.00, 1.00, 1.00, 1.00 ); >;
	float g_flFoamDepth < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 50 ); Range1( 0, 500 ); >;
	float g_flFoamFalloff < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 1.5 ); Range1( 0.1, 5 ); >;
	float g_flFoamIntensity < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.8 ); Range1( 0, 10 ); >;
	float g_flFoamNoiseScale < UiGroup( "Foam,0/,0/0" ); Default1( 20 ); Range1( 1, 100 ); >;
	float g_flFoamNoiseSpeed < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.3 ); Range1( 0, 2 ); >;
	float g_flFoamSoftness < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 1.5 ); Range1( 0, 5 ); >;
	float g_flFoamCoverage < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.5 ); Range1( 0, 5 ); >;
	float g_flFoamEdgeWarp < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.5 ); Range1( 0, 1 ); >;
	float g_flFoamEdgeScale < UiGroup( "Foam,0/,0/0" ); Default1( 5 ); Range1( 0.5, 30 ); >;
	float g_flFoamEdgeSpeed < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.1 ); Range1( 0, 1 ); >;

	// ── Fresnel ──
	float g_flFresnelBias < UiType( Slider ); UiGroup( "Fresnel,0/,0/0" ); Default1( 0.02 ); Range1( 0, 1 ); >;
	float g_flFresnelPower < UiType( Slider ); UiGroup( "Fresnel,0/,0/0" ); Default1( 5 ); Range1( 0.5, 10 ); >;
	float4 g_vFresnelColor < UiType( Color ); UiGroup( "Fresnel,0/,0/0" ); Default4( 0.60, 0.85, 0.90, 1.00 ); >;

    // ── SSR Reflection ──
    bool g_bUseScreenSpaceReflection < UiGroup("Reflection,0/,0/10"); Default1(1); > ;
	float g_flReflectionStrength < UiType( Slider ); UiGroup( "Reflection,0/,0/20" ); Default1( 0.5 ); Range1( 0.1, 1 ); >;
	float g_flFoamReflectionStrength < UiType( Slider ); UiGroup( "Reflection,0/,0/30" ); Default1( 0 ); Range1( 0, 1 ); >;
    float g_flReflectionStepSize < UiType(Slider); UiGroup("Reflection,0/,0/40"); Default1(200.0); Range1(10, 2000); > ;

	// ── Surface ──
    float g_flRoughness < UiType(Slider); UiGroup("Surface,0/,0/0"); Default1(0); Range1(0, 1); > ;
    float g_flContrast < UiType(Slider); UiGroup("Surface,0/,0/0"); Default1(1); Range1(0, 2); > ;

    bool g_bRequireWaterInclusionVolumes < Attribute("RequireWaterInclusionVolumes"); Default(0); > ;

    bool g_bUseHybridInclusionBounds < Attribute("UseHybridInclusionBounds"); Default1(0); > ;
    float2 g_vHybridInclusionBoundsMin < Attribute("HybridInclusionBoundsMin"); Default2(0, 0); > ;
    float2 g_vHybridInclusionBoundsMax < Attribute("HybridInclusionBoundsMax"); Default2(0, 0); > ;



	// ─────────────────────────────────────────────────────────────────────────
	// Utility: Blend two normals (Reoriented Normal Mapping simplified)
	// ─────────────────────────────────────────────────────────────────────────
	float3 BlendNormals(float3 _A, float3 _B)
	{
		return normalize(float3(_A.xy + _B.xy, _A.z * _B.z));
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Utility: No-tile texture sampling (Inigo Quilez technique)
	// Samples the texture 4 times with random flips and offsets per tile,
	// then blends smoothly to eliminate visible repetition patterns
	// ─────────────────────────────────────────────────────────────────────────
	float4 Hash4(float2 _P)
	{
		return frac(sin(float4(
			1.0 + dot(_P, float2(37.0, 17.0)),
			2.0 + dot(_P, float2(11.0, 47.0)),
			3.0 + dot(_P, float2(41.0, 29.0)),
			4.0 + dot(_P, float2(23.0, 31.0))
		)) * 103.0);
	}

	float3 SampleNormalNoTile(Texture2D _Tex, SamplerState _Sampler, float2 _UV)
	{
		float2 iuv = floor(_UV);
		float2 fuv = frac(_UV);

		// Generate 4 random transforms for neighboring tiles
		float4 ofa = Hash4(iuv + float2(0, 0));
		float4 ofb = Hash4(iuv + float2(1, 0));
		float4 ofc = Hash4(iuv + float2(0, 1));
		float4 ofd = Hash4(iuv + float2(1, 1));

		// Random flips and offsets
		ofa.zw = sign(ofa.zw - 0.5);
		ofb.zw = sign(ofb.zw - 0.5);
		ofc.zw = sign(ofc.zw - 0.5);
		ofd.zw = sign(ofd.zw - 0.5);

		// Compute transformed UVs and derivatives for correct mipmapping
		float2 uvddx = ddx(_UV);
		float2 uvddy = ddy(_UV);

		float2 uva = _UV * ofa.zw + ofa.xy;
		float2 uvb = _UV * ofb.zw + ofb.xy;
		float2 uvc = _UV * ofc.zw + ofc.xy;
		float2 uvd = _UV * ofd.zw + ofd.xy;

		// Sample with proper gradient for mipmapping
		float3 sa = DecodeNormal(_Tex.SampleGrad(_Sampler, uva, uvddx * ofa.zw, uvddy * ofa.zw).xyz);
		float3 sb = DecodeNormal(_Tex.SampleGrad(_Sampler, uvb, uvddx * ofb.zw, uvddy * ofb.zw).xyz);
		float3 sc = DecodeNormal(_Tex.SampleGrad(_Sampler, uvc, uvddx * ofc.zw, uvddy * ofc.zw).xyz);
		float3 sd = DecodeNormal(_Tex.SampleGrad(_Sampler, uvd, uvddx * ofd.zw, uvddy * ofd.zw).xyz);

		// Flip normals XY to match UV flips
		sa.xy *= ofa.zw;
		sb.xy *= ofb.zw;
		sc.xy *= ofc.zw;
		sd.xy *= ofd.zw;

		// Smooth blend between the 4 samples
		float2 b = smoothstep(0.25, 0.75, fuv);
		return normalize(lerp(lerp(sa, sb, b.x), lerp(sc, sd, b.x), b.y));
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Utility: Color Dodge blend mode (Photoshop-style)
	// ─────────────────────────────────────────────────────────────────────────
	float ColorDodge(float _Base, float _Blend)
	{
		if (_Base <= 0.0f) return 0.0f;
		if (_Blend >= 1.0f) return 1.0f;

		return saturate(_Base / (1.0f - _Blend));
	}

	float3 ColorDodge3(float3 _Base, float3 _Blend)
	{
		return float3(
			ColorDodge(_Base.r, _Blend.r),
			ColorDodge(_Base.g, _Blend.g),
			ColorDodge(_Base.b, _Blend.b)
		);
	}

	float4 ColorDodge4(float4 _Base, float4 _Blend, bool _BlendAlpha = false)
	{
		return float4(
			ColorDodge3(_Base.rgb, _Blend.rgb).rgb,
			_BlendAlpha ? ColorDodge(_Base.a, _Blend.a) : max(_Base.a, _Blend.a)
		);
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Main pixel shader
	// ─────────────────────────────────────────────────────────────────────────
	float4 MainPs(PixelInput i) : SV_Target0
	{
		Material m = Material::Init(i);
		m.Albedo = float3(1, 1, 1);
		m.Normal = float3(0, 0, 1);
		m.Roughness = 1;
		m.Metalness = 0;
		m.AmbientOcclusion = 1;
		m.TintMask = 1;
		m.Opacity = 1;
		m.Emission = float3(0, 0, 0);
		m.Transmission = 0;



		// ── Dual scrolling normals ──────────────────────────────────────
		float mainNormalOffset = g_flTime / g_flSpeedMainNormal;
		float2 mainNormalUV = TileAndOffsetUv(i.vTextureCoords.xy, g_vNormalTiling, float2(mainNormalOffset, mainNormalOffset));
		float3 mainNormal = TransformNormal(SampleNormalNoTile(g_tMainNormal, g_sAniso, mainNormalUV), i.vNormalWs, i.vTangentUWs, i.vTangentVWs);

		float secondNormalOffset = g_flTime / g_flSpeedSecondNormal;
        float2 secondNormalUV = TileAndOffsetUv(i.vTextureCoords.xy, g_vNormalTiling, float2(secondNormalOffset, secondNormalOffset));
        float3 secondNormal = TransformNormal(SampleNormalNoTile(g_tSecondNormal, g_sAniso, secondNormalUV), i.vNormalWs, i.vTangentUWs, i.vTangentVWs);

		float3 blendedNormal = BlendNormals(mainNormal, secondNormal);

        float3 surfacePos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
        bool withinHybridInclusionBounds = !g_bUseHybridInclusionBounds ||
			(surfacePos.x >= g_vHybridInclusionBoundsMin.x && surfacePos.x <= g_vHybridInclusionBoundsMax.x &&
			 surfacePos.y >= g_vHybridInclusionBoundsMin.y && surfacePos.y <= g_vHybridInclusionBoundsMax.y);

		// ── Hull masking ────────────────────────────────────────────────
		if (withinHybridInclusionBounds && g_bRequireWaterInclusionVolumes && g_iWaterInclusionVolumeCount <= 0)
			discard;

		if (withinHybridInclusionBounds && g_iWaterInclusionVolumeCount > 0 && CheckWaterInclusionVolume(surfacePos) < 0.5)
			discard;

		// Discard water pixels inside any WaterExclusionVolume (e.g. boat hulls).
		// Checked before any texture sampling for early exit performance.
		if (CheckWaterExclusionVolume(surfacePos) > 0.5)
			discard;

		if (CheckWaterHullExclusion(surfacePos) > 0.5)
			discard;
		// ───────────────────────────────────────────────────────────────

		float2 screenUV = CalculateViewportUv(i.vPositionSs.xy);

		// Initial depth at original screen position (used for refraction fade only)
		float3 scenePosRaw = Depth::GetWorldPosition(i.vPositionSs.xy);
		float rawWaterDepth = max(surfacePos.z - scenePosRaw.z, 0);

		// Compute refraction UV offset, faded by camera distance
		float cameraDist = length(surfacePos - g_vCameraPositionWs);
		float distanceScale = 1.0 / max(cameraDist * 0.01, 1.0);
		float3 refractionOffset = blendedNormal * g_flRefractionStrength * distanceScale;
		float2 distortedUV = refractionOffset.xy + screenUV;

		// Fall back to undistorted UV if refraction goes outside screen bounds
		bool outOfBounds = any(distortedUV < 0.0) || any(distortedUV > 1.0);

		// Check if the refracted UV samples above-water geometry — if so, fall back
		// to the undistorted UV to prevent above-water objects leaking into the water
		float2 safeSS = outOfBounds ? i.vPositionSs.xy : (distortedUV * g_vRenderTargetSize);
		float3 scenePos = Depth::GetWorldPosition(safeSS);
		bool refractedAboveWater = scenePos.z > surfacePos.z;
		float2 finalSampleUV = (refractedAboveWater || outOfBounds) ? screenUV : distortedUV;

        float4 sceneColor = g_tFrameBufferCopyTexture.Sample(g_sBilinearMirror, finalSampleUV);

		// Use depth matching the sampled position
		float3 finalScenePos = refractedAboveWater ? scenePosRaw : scenePos;
		float waterDepth = max(surfacePos.z - finalScenePos.z, 0);



		// ── Depth-based water coloring ──────────────────────────────────
		float normalizedDepth = saturate(waterDepth / (g_flDepthMax * g_flDepthMultiplier));
		float depthGradient = pow(normalizedDepth, g_flDepthFalloff);
		float4 waterColor = lerp(g_vShallowColor, g_vDeepColor, depthGradient);



		// ── Blend refracted scene with water color ──────────────────────
		// In shallow water you see the refracted scene, in deep water it fades to water color
		float depthBlendFactor = saturate(depthGradient * g_flDepthBlend);
		float4 refractedColor = saturate(lerp(sceneColor, waterColor, depthBlendFactor));



		// ── Surface caustics (Voronoi noise + Color Dodge) ──────────────
		// Animated voronoi noise creates a shimmering caustic pattern on the surface
		float2 causticsTiling = g_vNormalTiling * float2(g_flCausticsTilingMultiplier, g_flCausticsTilingMultiplier);
		float causticsScrollOffset = g_flTime * g_flCausticsScrollSpeed;
		float2 causticsUV = TileAndOffsetUv(i.vTextureCoords.xy, causticsTiling, float2(causticsScrollOffset, causticsScrollOffset));

		float causticsAnimTime = g_flTime * g_flCausticsAnimSpeed;
		float causticsNoise = VoronoiNoise(causticsUV, causticsAnimTime, 10);
		float causticsPattern = smoothstep(g_flCausticsThresholdMin, g_flCausticsThresholdMax, causticsNoise);

		float4 causticsOverlay = float4(causticsPattern, causticsPattern, causticsPattern, causticsPattern);
		float4 causticsColor = saturate(lerp(refractedColor, ColorDodge4(refractedColor, causticsOverlay), g_flCausticsIntensity));

        float3 viewDirNorm = normalize(g_vCameraPositionWs - surfacePos);

        float foamMask = 0.0;
        float4 finalColor = causticsColor;
        
		[branch]
		if (waterDepth < g_flFoamDepth * (1.0 + g_flFoamEdgeWarp))
        {
            // ── Edge foam (multi-octave inverted Voronoi with UV warping) ──
            // Where waterDepth is small (near geometry edges), blend in animated foam
            // using warped, time-morphing voronoi noise for organic bubble clusters
            // Foam stays solid through most of its width, then drops off sharply at the inner edge
            // g_flFoamFalloff controls how sharp that inner cutoff is (higher = sharper)
            // Animated noise warps the foam edge so it's not a uniform strip
            // It makes the foam band wider in some spots and narrower in others over time
            float2 edgeWarpUV = i.vTextureCoords.xy * g_flFoamEdgeScale * g_vNormalTiling.x;
            float edgeWarpTime = g_flTime * g_flFoamEdgeSpeed;
            float edgeWarp = Simplex2D(edgeWarpUV + float2(edgeWarpTime * 0.7, edgeWarpTime * 0.4)) * 0.5 + 0.5;
            float warpedFoamDepth = g_flFoamDepth * lerp(1.0 - g_flFoamEdgeWarp, 1.0 + g_flFoamEdgeWarp, edgeWarp);

            // Use refracted depth so foam matches the refracted scene underneath
            // preventing a white ghost duplicate at the original object position
            float foamNormDepth = saturate(waterDepth / max(warpedFoamDepth, 0.001));
            float foamFalloffStart = 1.0 - (1.0 / max(g_flFoamFalloff + 1.0, 1.0));
            float foamDepthMask = 1.0 - smoothstep(foamFalloffStart, 1.0, foamNormDepth);

            float foamTime = g_flTime * g_flFoamNoiseSpeed;
            float2 foamBaseUV = i.vTextureCoords.xy * g_flFoamNoiseScale * g_vNormalTiling.x;

            // Two simplex layers scrolling in opposing directions — their interference
            // creates blobs that merge, split, and reform naturally like real foam
            float2 foamUV1 = foamBaseUV + float2(foamTime * 0.6, foamTime * 0.3);
            float2 foamUV2 = foamBaseUV * 1.4 + float2(-foamTime * 0.4, foamTime * 0.5) + float2(3.7, 8.2);
            float foamLayer1 = Simplex2D(foamUV1) * 0.5 + 0.5;
            float foamLayer2 = Simplex2D(foamUV2) * 0.5 + 0.5;

            // Combine — where both layers overlap we get foam, as they drift apart foam dissolves
            float foamCombined = foamLayer1 * foamLayer2;

            // Time-varying threshold — a slow third noise makes foam patches swell and shrink
            float foamThreshold = Simplex2D(foamBaseUV * 0.2 + float2(foamTime * 0.08, -foamTime * 0.05)) * 0.1 + 0.15;

            // Wide smoothstep range for a soft, gradual transition between foam and water
            float foamNoise = smoothstep(foamThreshold - g_flFoamSoftness, foamThreshold + g_flFoamCoverage, foamCombined);

            // Combine depth mask with noise
            // Fade by view angle: at grazing angles the depth buffer is unreliable (waterDepth → 0
            // → foamDepthMask → 1), causing foam to cover the entire surface. NdotV naturally
            // reaches 0 at horizontal and goes negative from underwater (clamped to 0), eliminating
            // the artifact without affecting top-down or moderate-angle views.
            float foamViewFade = saturate(dot(i.vNormalWs, viewDirNorm));

            foamMask = saturate(foamDepthMask * foamNoise) * g_flFoamIntensity * foamViewFade;
            finalColor = lerp(causticsColor, g_vFoamColor, foamMask);
        }


		
		// ── Fresnel ─────────────────────────────────────────────────────
		// Water is transparent when looking straight down, reflective at grazing angles
		// Schlick's approximation: F = bias + (1 - bias) * (1 - cos(theta))^power
        float NdotV = saturate(dot(i.vNormalWs, viewDirNorm));
        float fresnel = g_flFresnelBias + (1.0 - g_flFresnelBias) * pow(1.0 - NdotV, g_flFresnelPower);
		finalColor = lerp(finalColor, g_vFresnelColor, fresnel);



        // ── SSR Reflection ───────────────────────────────────────────────
		if (g_bUseScreenSpaceReflection)
        {
            float3 lerpedNormal = normalize(lerp(i.vNormalWs, blendedNormal, g_flNormalStrength));
            float3 reflDirWs = reflect(-viewDirNorm, lerpedNormal);

            float viewDot = saturate(dot(lerpedNormal, viewDirNorm));
            float angleFactor = lerp(1.5, 0.2, viewDot);
            float heightFactor = saturate(abs(surfacePos.z - g_vCameraPositionWs.z) * 0.01);
            float stepSize = g_flReflectionStepSize * angleFactor * (0.5 + heightFactor);

            float3 virtualHit = surfacePos + reflDirWs * stepSize;
            float4 clipPos = Position3WsToPs(virtualHit);
            float2 reflUV = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
            reflUV.y = 1.0 - reflUV.y;

            bool validUV = all(reflUV >= 0.0) && all(reflUV <= 1.0);
            float2 edgeDist = abs(reflUV - 0.5) * 2.0;
            float ssrWeight = validUV ? (1.0 - smoothstep(0.7, 1.0, max(edgeDist.x, edgeDist.y))) : 0.0;

            if (ssrWeight > 0.0)
            {
                float3 ssrColor = g_tFrameBufferCopyTexture.Sample(g_sPointClamp, reflUV).rgb;
                float foamReflMod = lerp(1.0, g_flFoamReflectionStrength, foamMask);
                float reflAmount = fresnel * g_flReflectionStrength * ssrWeight * foamReflMod;
                finalColor.rgb = lerp(finalColor.rgb, ssrColor, reflAmount);
            }
        }



		// ── Apply normal strength ───────────────────────────────────────
		// Scale XY components of the blended normal, lerp Z toward 1 (flat) when strength < 1
		float3 scaledNormal = float3(
			blendedNormal.x * g_flNormalStrength,
			blendedNormal.y * g_flNormalStrength,
			lerp(1, blendedNormal.z, saturate(g_flNormalStrength))
		);



		// ── Output ──────────────────────────────────────────────────────
		finalColor.rgb = saturate((finalColor.rgb - 0.5) * g_flContrast + 0.5);

		m.Albedo = finalColor.rgb;
		// Shore transparency — fades water opacity near the coast independently of color alpha
		float shoreBlend = saturate(rawWaterDepth / max(g_flShoreOpacityRange, 0.001));
        float shoreOpacity = lerp(g_flShoreOpacity, 1.0, shoreBlend);
        m.Opacity = shoreOpacity * waterColor.a;
		m.Normal = scaledNormal;
		m.Roughness = g_flRoughness;

		m.Roughness = saturate(m.Roughness);
		m.Opacity = saturate(m.Opacity);
		
		return ShadingModelStandard::Shade(m);
	}
}
