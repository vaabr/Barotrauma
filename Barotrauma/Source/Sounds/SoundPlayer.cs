﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using Barotrauma.Sounds;
using System.Collections.Generic;
using System.IO;

namespace Barotrauma
{
    public enum DamageSoundType 
    { 
        None, 
        StructureBlunt, StructureSlash, 
        LimbBlunt, LimbSlash, LimbArmor
    }

    public struct DamageSound
    {
        //the range of inflicted damage where the sound can be played
        //(10.0f, 30.0f) would be played when the inflicted damage is between 10 and 30
        public readonly Vector2 damageRange;

        public readonly DamageSoundType damageType;

        public readonly Sound sound;

        public readonly string requiredTag;

        public DamageSound(Sound sound, Vector2 damageRange, DamageSoundType damageType, string requiredTag = "")
        {
            this.sound = sound;
            this.damageRange = damageRange;
            this.damageType = damageType;

            this.requiredTag = requiredTag;
        }
    }

    public class BackgroundMusic
    {
        public readonly string file;
        public readonly string type;

        public readonly Vector2 priorityRange;

        public BackgroundMusic(string file, string type, Vector2 priorityRange)
        {
            this.file = file;
            this.type = type;
            this.priorityRange = priorityRange;
        }
    }

    static class SoundPlayer
    {
        private static ILookup<string, Sound> miscSounds;

        //music
        public static float MusicVolume = 1.0f;
        private const float MusicLerpSpeed = 0.1f;

        private static BackgroundMusic currentMusic;
        private static BackgroundMusic targetMusic;
        private static BackgroundMusic[] musicClips;
        private static float currMusicVolume;

        //ambience
        private static Sound[] waterAmbiences = new Sound[2];
        private static int[] waterAmbienceIndexes = new int[2];

        private static float ambientSoundTimer;
        private static Vector2 ambientSoundInterval = new Vector2(20.0f, 40.0f); //x = min, y = max

        //misc
        public static Sound[] flowSounds = new Sound[3];
        public static Sound[] SplashSounds = new Sound[10];

        private static List<DamageSound> damageSounds;

        private static Sound startDrone;

        public static bool Initialized;

        public static string OverrideMusicType
        {
            get;
            set;
        }

        public static int SoundCount;
        
        public static IEnumerable<object> Init()
        {
            OverrideMusicType = null;

            XDocument doc = ToolBox.TryLoadXml("Content/Sounds/sounds.xml");
            if (doc == null) yield return CoroutineStatus.Failure;

            SoundCount = 16 + doc.Root.Elements().Count();

            startDrone = Sound.Load("Content/Sounds/startDrone.ogg", false);
            startDrone.Play();

            yield return CoroutineStatus.Running;

            waterAmbiences[0] = Sound.Load("Content/Sounds/Water/WaterAmbience1.ogg", false);
            yield return CoroutineStatus.Running;
            waterAmbiences[1] = Sound.Load("Content/Sounds/Water/WaterAmbience2.ogg", false);
            yield return CoroutineStatus.Running;
            flowSounds[0] = Sound.Load("Content/Sounds/Water/FlowSmall.ogg", false);
            yield return CoroutineStatus.Running;
            flowSounds[1] = Sound.Load("Content/Sounds/Water/FlowMedium.ogg", false);
            yield return CoroutineStatus.Running;
            flowSounds[2] = Sound.Load("Content/Sounds/Water/FlowLarge.ogg", false);
            yield return CoroutineStatus.Running;

            for (int i = 0; i < 10; i++ )
            {
                SplashSounds[i] = Sound.Load("Content/Sounds/Water/Splash"+(i)+".ogg", false);
                yield return CoroutineStatus.Running;
            }
            
            var xMusic = doc.Root.Elements("music").ToList();

            if (xMusic.Any())
            {
                musicClips = new BackgroundMusic[xMusic.Count];
                int i = 0;
                foreach (XElement element in xMusic)
                {
                    string file = ToolBox.GetAttributeString(element, "file", "");
                    string type = ToolBox.GetAttributeString(element, "type", "").ToLowerInvariant();
                    Vector2 priority = ToolBox.GetAttributeVector2(element, "priorityrange", new Vector2(0.0f, 100.0f));

                    musicClips[i] = new BackgroundMusic(file, type, priority);

                    yield return CoroutineStatus.Running;

                    i++;
                }
            }

            List<KeyValuePair<string, Sound>> miscSoundList = new List<KeyValuePair<string, Sound>>();
            damageSounds = new List<DamageSound>();
            
            foreach (XElement subElement in doc.Root.Elements())
            {
                yield return CoroutineStatus.Running;

                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "music":
                        continue;
                    case "damagesound":
                        Sound damageSound = Sound.Load(ToolBox.GetAttributeString(subElement, "file", ""), false);
                        if (damageSound == null) continue;
                    
                        DamageSoundType damageSoundType = DamageSoundType.None;
                        Enum.TryParse<DamageSoundType>(ToolBox.GetAttributeString(subElement, "damagesoundtype", "None"), false, out damageSoundType);

                        damageSounds.Add(new DamageSound(
                            damageSound, 
                            ToolBox.GetAttributeVector2(subElement, "damagerange", new Vector2(0.0f, 100.0f)), 
                            damageSoundType, 
                            ToolBox.GetAttributeString(subElement, "requiredtag", "")));

                        break;
                    default:
                        Sound sound = Sound.Load(ToolBox.GetAttributeString(subElement, "file", ""), false);
                        if (sound != null)
                        {
                            miscSoundList.Add(new KeyValuePair<string, Sound>(subElement.Name.ToString().ToLowerInvariant(), sound));
                        }

                        break;
                }
            }

            miscSounds = miscSoundList.ToLookup(kvp => kvp.Key, kvp => kvp.Value);            

            Initialized = true;

            yield return CoroutineStatus.Success;

        }
        

        public static void Update()
        {
            UpdateMusic();

            if (startDrone != null && !startDrone.IsPlaying)
            {
                startDrone.Remove();
                startDrone = null;                
            }

            //stop submarine ambient sounds if no sub is loaded
            if (Submarine.MainSub == null)  
            {
                for (int i = 0; i < waterAmbienceIndexes.Length; i++)
                {
                    if (waterAmbienceIndexes[i] <= 0) continue;

                    SoundManager.Stop(waterAmbienceIndexes[i]);
                    waterAmbienceIndexes[i] = 0;
                }  
                return;
            }

            float ambienceVolume = 0.8f;
            float lowpassHFGain = 1.0f;
            if (Character.Controlled != null)
            {
                AnimController animController = Character.Controlled.AnimController;
                if (animController.HeadInWater)
                {
                    ambienceVolume = 1.0f;
                    ambienceVolume += animController.Limbs[0].LinearVelocity.Length();

                    lowpassHFGain = 0.2f;
                }

                lowpassHFGain *= Character.Controlled.LowPassMultiplier;
            }

            //how fast the sub is moving, scaled to 0.0 -> 1.0
            float movementSoundVolume = 0.0f;

            foreach (Submarine sub in Submarine.Loaded)
            {
                float movementFactor = (sub.Velocity == Vector2.Zero) ? 0.0f : sub.Velocity.Length() / 10.0f;
                movementFactor = MathHelper.Clamp(movementFactor, 0.0f, 1.0f);

                if (Character.Controlled==null || Character.Controlled.Submarine != sub)
                {
                    float dist = Vector2.Distance(GameMain.GameScreen.Cam.WorldViewCenter, sub.WorldPosition);
                    movementFactor = movementFactor / Math.Max(dist / 1000.0f, 1.0f);
                }

                movementSoundVolume = Math.Max(movementSoundVolume, movementFactor);
            }

            if (ambientSoundTimer > 0.0f)
            {
                ambientSoundTimer -= (float)Timing.Step;
            }
            else
            {
                PlaySound(
                    "ambient",
                    Rand.Range(0.5f, 1.0f), 
                    1000.0f, 
                    new Vector2(Sound.CameraPos.X, Sound.CameraPos.Y) + Rand.Vector(100.0f));

                ambientSoundTimer = Rand.Range(ambientSoundInterval.X, ambientSoundInterval.Y);
            }

            SoundManager.LowPassHFGain = lowpassHFGain;
            waterAmbienceIndexes[0] = waterAmbiences[0].Loop(waterAmbienceIndexes[0], ambienceVolume * (1.0f - movementSoundVolume));
            waterAmbienceIndexes[1] = waterAmbiences[1].Loop(waterAmbienceIndexes[1], ambienceVolume * movementSoundVolume);

        }

        public static Sound GetSound(string soundTag)
        {
            var matchingSounds = miscSounds[soundTag].ToList();
            if (matchingSounds.Count == 0) return null;

            return matchingSounds[Rand.Int(matchingSounds.Count)];
        }

        public static void PlaySound(string soundTag, float volume = 1.0f)
        {
            var sound = GetSound(soundTag);            
            if (sound != null) sound.Play(volume);
        }

        public static void PlaySound(string soundTag, float volume, float range, Vector2 position)
        {
            var sound = GetSound(soundTag);
            if (sound != null) sound.Play(volume, range, position);
        }

        private static void UpdateMusic()
        {
            if (musicClips == null) return;
            
            List<BackgroundMusic> suitableMusic = GetSuitableMusicClips();

            if (suitableMusic.Count == 0)
            {
                targetMusic = null;
            }                
            else if (!suitableMusic.Contains(currentMusic))
            {
                int index = Rand.Int(suitableMusic.Count);

                if (currentMusic == null || suitableMusic[index].file != currentMusic.file)
                {
                    targetMusic = suitableMusic[index];
                }
            }

            if (targetMusic == null || currentMusic == null || targetMusic.file != currentMusic.file)
            {
                currMusicVolume = MathHelper.Lerp(currMusicVolume, 0.0f, MusicLerpSpeed);
                if (currentMusic != null) Sound.StreamVolume(currMusicVolume);

                if (currMusicVolume < 0.01f)
                {
                    Sound.StopStream();

                    try
                    {
                        if (targetMusic != null) Sound.StartStream(targetMusic.file, currMusicVolume);
                    }
                    catch (FileNotFoundException e)
                    {
                        DebugConsole.ThrowError("Music clip " + targetMusic.file + " not found!", e);
                    }

                    currentMusic = targetMusic;
                }
            }
            else
            {
                currMusicVolume = MathHelper.Lerp(currMusicVolume, MusicVolume, MusicLerpSpeed);
                Sound.StreamVolume(currMusicVolume);
            }
        }

        public static void SwitchMusic()
        {
            var suitableMusic = GetSuitableMusicClips();

            if (suitableMusic.Count > 1)
            {
                targetMusic = suitableMusic.Find(m => m != currentMusic);
            }
        }

        private static List<BackgroundMusic> GetSuitableMusicClips()
        {
            string musicType = "default";
            if (OverrideMusicType != null)
            {
                musicType = OverrideMusicType;
            }
            else if (Character.Controlled != null &&
                Level.Loaded != null && Level.Loaded.Ruins != null &&
                Level.Loaded.Ruins.Any(r => r.Area.Contains(Character.Controlled.WorldPosition)))
            {
                musicType = "ruins";
            }
            else if ((Character.Controlled != null && Character.Controlled.Submarine != null && Character.Controlled.Submarine.AtDamageDepth) ||
                    (Screen.Selected == GameMain.GameScreen && GameMain.GameScreen.Cam.Position.Y < SubmarineBody.DamageDepth))
            {
                musicType = "deep";
            }
            else
            {
                Task criticalTask = null;
                if (GameMain.GameSession != null && GameMain.GameSession.TaskManager != null)
                {
                    foreach (Task task in GameMain.GameSession.TaskManager.Tasks)
                    {
                        if (!task.IsStarted) continue;
                        if (criticalTask == null || task.Priority > criticalTask.Priority)
                        {
                            criticalTask = task;
                        }
                    }
                }

                if (criticalTask != null)
                {
                    var suitableClips =
                        musicClips.Where(music =>
                            music != null &&
                            music.type == criticalTask.MusicType &&
                            music.priorityRange.X < criticalTask.Priority &&
                            music.priorityRange.Y > criticalTask.Priority).ToList();

                    if (suitableClips.Count > 0) return suitableClips;
                }
            }

            return musicClips.Where(music => music != null && music.type == musicType).ToList();
        }

        public static void PlaySplashSound(Vector2 worldPosition, float strength)
        {
            int splashIndex = MathHelper.Clamp((int)(strength + Rand.Range(-2,2)), 0, SplashSounds.Length-1);

            SplashSounds[splashIndex].Play(1.0f, 800.0f, worldPosition);
        }

        public static void PlayDamageSound(DamageSoundType damageType, float damage, PhysicsBody body)
        {
            Vector2 bodyPosition = body.DrawPosition;

            PlayDamageSound(damageType, damage, bodyPosition, 800.0f);
        }

        public static void PlayDamageSound(DamageSoundType damageType, float damage, Vector2 position, float range = 2000.0f, List<string> tags = null)
        {
            damage = MathHelper.Clamp(damage+Rand.Range(-10.0f, 10.0f), 0.0f, 100.0f);
            var sounds = damageSounds.FindAll(s => 
                damage >= s.damageRange.X && 
                damage <= s.damageRange.Y && 
                s.damageType == damageType &&
                (string.IsNullOrEmpty(s.requiredTag) || (tags != null && tags.Contains(s.requiredTag))));

            if (!sounds.Any()) return;

            int selectedSound = Rand.Int(sounds.Count);

            sounds[selectedSound].sound.Play(1.0f, range, position);
            Debug.WriteLine("playing: " + sounds[selectedSound].sound);
        }
        
    }
}