﻿
using System;
using System.Collections.Generic;
using System.Security.Permissions;
using UdonSharp;
using UdonToolkit;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace VirtualAviationJapan
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(AudioSource))]
    public class ATISPlayer : UdonSharpBehaviour
    {
        public const int MAX_WORDS = 256;
        public const int STATE_PREFIX = 0;
        public const int STATE_PREFIX_PHONETIC = 1;
        public const int STATE_TIME = 2;
        public const int STATE_TIME_ZONE = 3;
        public const int STATE_RUNWAY = 4;
        public const int STATE_WIND_WIND = 5;
        public const int STATE_WIND_DIRECTION = 6;
        public const int STATE_WIND_DEGREES = 7;
        public const int STATE_WIND_SPEED = 8;
        public const int STATE_WIND_KNOT = 9;
        public const int STATE_SUFFIX = 10;
        public const int STATE_SUFFIX_PHONETIC = 11;

        public const int STATE_START = STATE_PREFIX;
        public const int STATE_END = STATE_SUFFIX_PHONETIC;
        public const int PLAY_MODE_DIGITS = 1;
        public const int PLAY_MODE_NUMERIC = 2;

        public const float KNOTS = 1.944f;

        [TextArea] public string template = "AERODROME information [{0}] [{1}] [Z]. {2}. wind {3}. visibility 10 kilometers, sky clear. temperature [25], dewpoint [20], qnh [29] decimal [92]. advise you have information [{0}]";
        [ListView("Runway Operations")] public float[] windHeadings = { 0.0f, 180.0f };
        [ListView("Runway Operations")]
        public string[] runwayTemplates = {
            "ils runway [36] approach. using runway [36].",
            "ils runway [18] approach. using runway [18].",
        };
        public string windTemplate = "[{0:000}] degrees {1:0} knots";

        public UdonSharpBehaviour windSource;
        [Popup("programVariable", "@windSource", "vector")] public string windVariableName = "Wind";
        [Tooltip("Knots")] public float minWind = 5;

        public AudioClip[] digits = { }, phonetics = { };
        public AudioClip periodInterval, repeatInterval;
        [ListView("Vocabulary")] public string[] clipWords = { };
        [ListView("Vocabulary")] public AudioClip[] clips = { };

        private AudioSource audioSource;
        private Vector3 windVector;
        private float windSpeed;
        private bool windCalm;
        private int windHeading;
        private float magneticDeclination;
        private AudioClip[] words;
        private int wordIndex;
        private int informationIndex;

        private void Start()
        {
            audioSource = GetComponent<AudioSource>();

            var navaidDatabaseObj = GameObject.Find("NavaidDatabase");
            if (navaidDatabaseObj) magneticDeclination = (float)((UdonBehaviour)navaidDatabaseObj.GetComponent(typeof(UdonBehaviour))).GetProgramVariable("magneticDeclination");

            UpdateInformation();
            wordIndex = words == null ? -1 : Time.frameCount % words.Length;
        }

        private void OnEnable()
        {
            wordIndex = words == null ? -1 : Time.frameCount % words.Length;
        }

        private void OnDisable()
        {
            if (audioSource) audioSource.Stop();
        }

        private void Update()
        {
            if (!audioSource) return;
            if (!audioSource.isPlaying) OnClipEnd();
        }

        public void _Play()
        {
            UpdateInformation();
            gameObject.SetActive(true);
        }

        public void _Stop()
        {
            gameObject.SetActive(false);
        }
        readonly private char[] trimChars = new[] { '[', ']', ',', '.', ' ' };

        private void UpdateInformation()
        {
            var now = DateTime.UtcNow;
            var hour = now.Hour;
            var minute = now.Minute / 30 * 30;

            var prevInformationIndex = informationIndex;
            informationIndex = (hour * 60 + minute) / 30 % ('Z' - 'A');

            var timestamp = string.Format("{0:00}{1:00}", hour, minute);

            if (prevInformationIndex != informationIndex || words == null)
            {
                windVector = windSource ? (Vector3)windSource.GetProgramVariable(windVariableName) : Vector3.zero;
                windSpeed = windVector.magnitude * KNOTS;
                windCalm = windSpeed < minWind;

                windHeading = Mathf.RoundToInt(Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(windVector, Vector3.up), Vector3.up) + magneticDeclination + 360 + 180) % 360;

                var windString = windCalm ? "calm" : string.Format(windTemplate, new object[] { windSpeed, windHeading });
                var runwayOperationIndex = windCalm ? 0 : IndexOfRunwayOperation(windHeading);

                var rawWords = string.Format(template, (char)('A' + informationIndex), timestamp, runwayTemplates[runwayOperationIndex], windString).Split(' ');
                var wordsBuf = new AudioClip[MAX_WORDS];
                var wordsBufIndex = 0;
                var period = false;
                foreach (var rawWord in rawWords)
                {
                    var word = rawWord.Trim(trimChars);
                    var chars = word.ToCharArray();
                    var firstChar = chars[0];

                    if (period)
                    {
                        wordsBuf[wordsBufIndex++] = periodInterval;
                    }
                    period = rawWord.EndsWith(".");

                    if (rawWord.StartsWith("["))
                    {
                        if (char.IsDigit(firstChar))
                        {
                            foreach (var c in chars)
                            {
                                wordsBuf[wordsBufIndex++] = GetDigitClip(c - '0');
                            }
                            continue;
                        }

                        if (char.IsUpper(firstChar))
                        {
                            wordsBuf[wordsBufIndex++] = GetPhoneticClip(firstChar);
                            continue;
                        }
                    }

                    if (char.IsDigit(firstChar))
                    {
                        int value;
                        if (int.TryParse(word, out value))
                        {
                            while (value >= 0)
                            {
                                if (value >= 1000)
                                {
                                    wordsBuf[wordsBufIndex++] = GetDigitClip(value / 1000);
                                    wordsBuf[wordsBufIndex++] = GetWordClip("thousand");
                                    value %= 1000;
                                }
                                else if (value >= 100)
                                {
                                    wordsBuf[wordsBufIndex++] = GetDigitClip(value / 100);
                                    wordsBuf[wordsBufIndex++] = GetWordClip("hundred");
                                    value %= 100;
                                }
                                else if (value >= 20)
                                {
                                    wordsBuf[wordsBufIndex++] = GetDigitClip(value / 10 * 10);
                                    value %= 10;
                                }
                                else if (value >= 10)
                                {
                                    wordsBuf[wordsBufIndex++] = GetDigitClip(value / 10 * 10);
                                    value = -1;
                                }
                                else
                                {
                                    wordsBuf[wordsBufIndex++] = GetDigitClip(value);
                                    value = -1;
                                }
                            }
                        }
                        continue;
                    }

                    var clip = GetWordClip(word);
                    if (clip)
                    {
                        wordsBuf[wordsBufIndex++] = clip;
                        continue;
                    }

                    Debug.LogWarning($"[Virtual-CNS][ATIS] Failed to find clip of word \"{rawWord}\" (\"{word}\").");
                }
                wordsBuf[wordsBufIndex++] = repeatInterval;

                words = new AudioClip[wordsBufIndex];
                Array.Copy(wordsBuf, words, words.Length);
            }
        }

        private AudioClip GetDigitClip(int value)
        {
            if (value >= 100) return null;
            if (value >= 20) return digits[value / 10 + 20];
            if (value >= 10) return digits[value];
            return digits[value];
        }

        private AudioClip GetPhoneticClip(char c)
        {
            return phonetics[c - 'A'];
        }

        private int IndexOfRunwayOperation(float windHeading)
        {
            var minDifference = float.MaxValue;
            var minIndex = 0;

            for (var i = 0; i < windHeadings.Length; i++)
            {
                var difference = Mathf.Abs(Mathf.DeltaAngle(windHeading, windHeadings[i]));
                if (difference < minDifference)
                {
                    minDifference = difference;
                    minIndex = i;
                }
            }

            return minIndex;
        }

        private AudioClip GetWordClip(string word)
        {
            for (var i = 0; i < clipWords.Length; i++)
            {
                if (clipWords[i] == word) return clips[i];
            }
            return null;
        }

        private void OnClipEnd()
        {
            wordIndex += 1;
            if (wordIndex >= words.Length)
            {
                UpdateInformation();
                wordIndex = 0;
            }
            PlayOneShot(words[wordIndex]);
        }

        private void PlayOneShot(AudioClip clip)
        {
            if (!audioSource || !clip) return;
            Debug.Log($"[Virtual-CNS][ATIS] Play: {clip}");
            audioSource.PlayOneShot(clip);
        }
    }
}