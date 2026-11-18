using SeaPower;

namespace SeaPowerFighterAI
{
    // Giving the flight controls back to the game after a mod behaviour (missile defence, notch, wingman extend,
    // post-defence search) borrowed them.
    //
    // In Sea Power 0.9.0 the game's own aircraft states own Motioncontroller.commandPositionOverride: states that steer to
    // a point they compute every frame (MoveToEngage, Loitering and its CAP/AEW-orbit subclasses, IdentifyAirContact,
    // MovingInFormation) switch it on when entered and off when left; every other state (Default, PlayerOverride for the
    // player's own orders, ReturnToBase, ...) expects it off. The state can change while the mod is flying the aircraft,
    // so restoring the value saved when the behaviour began can leave the override on with nothing updating the steering
    // point: the aircraft then flies at a stale point and ignores the player's orders. So restore what the CURRENT state
    // expects, and use the saved value only for states not listed here.
    internal static class Handback
    {
        internal static bool PositionOverride(ObjectBase ac, bool saved)
        {
            string state = ac?.StateMachine?.CurrentState?.GetType().Name;
            switch (state)
            {
                case "MoveToEngage":
                case "Loitering":
                case "CombatAirPatrol":
                case "AirborneEarlyWarningOrbit":
                case "IdentifyAirContact":
                case "MovingInFormation":
                    return true;
                case "Default":
                case "PlayerOverride":
                case "ReturnToBase":
                case "BingoFuel":
                case "Winchester":
                case "SelectMovementType":
                case "NoMotionController":
                    return false;
                default:
                    return saved;
            }
        }

        internal static string StateName(ObjectBase ac) => ac?.StateMachine?.CurrentState?.GetType().Name ?? "none";
    }
}
