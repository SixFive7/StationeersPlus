using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using LaunchPadBooster.Networking;
using System.Linq;

namespace EquipmentPlus
{
    public class SetActiveSensorMessage : INetworkMessage
    {
        public long LensesReferenceId;
        public long SensorReferenceId;
        // When the cycle lands on a chip, PowerOn=true (lenses become powered
        // if they weren't). When the cycle lands on the "off" slot, PowerOn
        // =false so the lenses stop draining power. The server applies this
        // authoritatively via Thing.set_OnOff, which goes through the
        // networked Interactable state machinery.
        public bool PowerOn;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(LensesReferenceId);
            writer.WriteInt64(SensorReferenceId);
            writer.WriteByte((byte)(PowerOn ? 1 : 0));
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            LensesReferenceId = reader.ReadInt64();
            SensorReferenceId = reader.ReadInt64();
            PowerOn = reader.ReadByte() != 0;
        }

        public void Process(long clientId)
        {
            if (!(Thing.Find(LensesReferenceId) is SensorLenses lenses))
                return;

            SensorProcessingUnit target = null;
            if (SensorReferenceId != 0)
            {
                target = Referencable.Find<SensorProcessingUnit>(SensorReferenceId);
                if (target == null)
                    return;
                if (!lenses.Slots.Any(s => s.Get() == target))
                    return;
            }

            lenses.Sensor = target;
            if (lenses.OnOff != PowerOn)
                lenses.OnOff = PowerOn;
            lenses.NetworkUpdateFlags |= SensorLensesSync.ActiveSensorFlag;
        }
    }
}
