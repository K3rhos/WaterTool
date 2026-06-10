using System.Collections.Generic;
using Sandbox;
using Sandbox.Audio;

namespace RedSnail.WaterTool;

public class SoundData
{
	public bool OriginalSoundOverride { get; set; }
	public float OriginalVolume { get; set; }
	public float OriginalPitch { get; set; }
}

public sealed class UnderwaterAudioManager : Component
{
	private bool m_IsUnderwater;
	private readonly Dictionary<SoundPointComponent, SoundData> m_SoundPoints = new Dictionary<SoundPointComponent, SoundData>();
	private readonly Dictionary<SoundHandle, SoundData> m_SoundHandles = new Dictionary<SoundHandle, SoundData>();
	
	[Property, Range(0, 1)] private float UnderwaterVolumeMultiplier { get; set; } = 0.8f;
	[Property, Range(0, 2)] private float UnderwaterPitchMultiplier { get; set; } = 0.5f;
	
	
	
	protected override void OnUpdate()
	{ 
		if (Scene.Camera is not CameraComponent camera)
			return;
		
		bool isUnderwater = WaterManager.IsPositionInsideAny(camera.WorldPosition);
		
		// When underwater we're checking everyframe for sound points or sound handles in the world
		// and applying specific volume and pitch
		if (isUnderwater)
		{
			foreach (SoundPointComponent soundPointComponent in Scene.GetAllComponents<SoundPointComponent>())
			{
				if (soundPointComponent.TargetMixer == Mixer.FindMixerByName("ui"))
					continue;
				
				SoundData soundData = new SoundData
				{
					OriginalSoundOverride = soundPointComponent.SoundOverride,
					OriginalVolume = soundPointComponent.Volume,
					OriginalPitch = soundPointComponent.Pitch
				};

				if (m_SoundPoints.TryAdd(soundPointComponent, soundData))
				{
					soundPointComponent.SoundOverride = true;
					soundPointComponent.Volume = soundData.OriginalVolume * UnderwaterVolumeMultiplier;
					soundPointComponent.Pitch = soundData.OriginalPitch * UnderwaterPitchMultiplier;
				}
			}
			
			List<SoundHandle> soundHandles = [];
			SoundHandle.GetActive(soundHandles);
			
			foreach (SoundHandle soundHandle in soundHandles)
			{
				if (soundHandle.TargetMixer == Mixer.FindMixerByName("ui"))
					continue;
				
				SoundData soundData = new SoundData
				{
					OriginalVolume = soundHandle.Volume,
					OriginalPitch = soundHandle.Pitch
				};
				
				if (m_SoundHandles.TryAdd(soundHandle, soundData))
				{
					soundHandle.Volume = soundData.OriginalVolume * UnderwaterVolumeMultiplier;
					soundHandle.Pitch = soundData.OriginalPitch * UnderwaterPitchMultiplier;
				}
			}
		}
		
		if (isUnderwater != m_IsUnderwater)
		{
			// If we just leave underwater
			if (!isUnderwater)
			{
				// We iterate in all the previously edited sound points and restore original values
				foreach (var (soundPointComponent, soundData) in m_SoundPoints)
				{
					soundPointComponent.SoundOverride = soundData.OriginalSoundOverride;
					soundPointComponent.Volume = soundData.OriginalVolume;
					soundPointComponent.Pitch = soundData.OriginalPitch;
				}
				
				// and also in all the previously edited sound handles and restore original values
				foreach (var (soundHandle, soundData) in m_SoundHandles)
				{
					soundHandle.Volume = soundData.OriginalVolume;
					soundHandle.Pitch = soundData.OriginalPitch;
				}
				
				// Make sure we're cleaning the lists
				m_SoundPoints.Clear();
				m_SoundHandles.Clear();
			}
			
			m_IsUnderwater = isUnderwater;
		}
	}
}
