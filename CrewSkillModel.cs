using System;
using UnityEngine;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Crew-skill scaling of the fighter behaviours (defensive and offensive). The skill comes from the aircraft's
    // Crew (ObjectBase.CrewSkill: Green, Trained, Seasoned, Veterans, Ultra). All numbers are tunables here.
    //
    //   ReactionSec     - mean delay between the aircraft knowing about an inbound missile and starting the defence.
    //   BotchChance     - chance, re-rolled every BotchRerollSec, that the notch/beam aim is off for that interval.
    //   BotchMaxDeg     - largest heading error of a botched interval (actual error is 40-100 % of this, random side).
    //   LastDitchLateSec- how much later than ideal the last-ditch max pull is applied.
    //   GateIgnore      - offensive: how much of the speed-aware launch-range reduction (LaunchRangeGatePatch) the
    //                     shooter ignores, i.e. 0 = disciplined, 1 = fires at the missile's full vanilla range.
    public static class CrewSkillModel
    {
        public static readonly bool Enabled = true;

        public const double BotchRerollSec = 10.0;

        public struct Profile
        {
            public float ReactionSec;
            public float BotchChance;
            public float BotchMaxDeg;
            public float LastDitchLateSec;
            public float GateIgnore;
        }

        private static readonly Profile Green = new Profile { ReactionSec = 6f, BotchChance = 0.35f, BotchMaxDeg = 30f, LastDitchLateSec = 0.6f, GateIgnore = 0.50f };
        private static readonly Profile Trained = new Profile { ReactionSec = 3f, BotchChance = 0.18f, BotchMaxDeg = 22f, LastDitchLateSec = 0.35f, GateIgnore = 0.25f };
        private static readonly Profile Seasoned = new Profile { ReactionSec = 1.5f, BotchChance = 0.08f, BotchMaxDeg = 15f, LastDitchLateSec = 0.15f, GateIgnore = 0.10f };
        private static readonly Profile Veteran = new Profile { ReactionSec = 0.5f, BotchChance = 0.03f, BotchMaxDeg = 10f, LastDitchLateSec = 0.05f, GateIgnore = 0f };
        private static readonly Profile Ultra = new Profile { ReactionSec = 0f, BotchChance = 0f, BotchMaxDeg = 0f, LastDitchLateSec = 0f, GateIgnore = 0f };

        private static readonly System.Random Rng = new System.Random();

        public static Profile For(ObjectBase unit)
        {
            if (!Enabled || unit == null || unit._crew == null)
            {
                return Ultra;
            }

            switch (unit._crew.OverallSkill)
            {
                case ObjectBase.CrewSkill.Green: return Green;
                case ObjectBase.CrewSkill.Trained: return Trained;
                case ObjectBase.CrewSkill.Seasoned: return Seasoned;
                case ObjectBase.CrewSkill.Veterans: return Veteran;
                default: return Ultra;
            }
        }

        public static double Uniform(double lo, double hi)
        {
            lock (Rng)
            {
                return lo + (hi - lo) * Rng.NextDouble();
            }
        }

        // Reaction delay for this crew: mean +/- 30 %.
        public static float RollReactionSec(ObjectBase unit) => For(unit).ReactionSec * (float)Uniform(0.7, 1.3);

        // Heading error (deg, signed) for the next BotchRerollSec interval; 0 when the crew gets it right.
        public static float RollAimErrorDeg(ObjectBase unit)
        {
            Profile p = For(unit);
            if (p.BotchChance <= 0f || Uniform(0, 1) >= p.BotchChance)
            {
                return 0f;
            }

            float side = Uniform(0, 1) < 0.5 ? -1f : 1f;
            return side * p.BotchMaxDeg * (float)Uniform(0.4, 1.0);
        }
    }
}
