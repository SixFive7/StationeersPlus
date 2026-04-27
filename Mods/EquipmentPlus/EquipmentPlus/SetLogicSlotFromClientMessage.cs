using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using LaunchPadBooster.Networking;

namespace EquipmentPlus
{
    // Client-to-server slot-logic write. Vanilla provides SetLogicFromClient for
    // (LogicType, double) writes but no equivalent for (LogicSlotType, slotIndex,
    // double) — slot writes through Device.SetLogicValue route to OnServer.Interact
    // on the slot occupant, which silently no-ops on a remote client because
    // GameManager.RunSimulation is false. This message is the equivalent for
    // slot writes.
    //
    // Authority: server validates and applies. Broadcast is implicit: applying
    // SetLogicValue triggers OnServer.Interact -> Interactable.Interact which
    // sets Parent.NetworkUpdateFlags |= 2 on the slot occupant, vanilla state-sync
    // replicates the occupant's interactable state, and the cartridge re-derives
    // the slot logic value on the next OnMainTick. No custom NetworkUpdateFlag
    // bit and no SerializeOnJoin extension are needed.
    public class SetLogicSlotFromClientMessage : INetworkMessage
    {
        public long DeviceId;
        public int SlotIndex;
        public int LogicSlotTypeInt;
        public double Value;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(DeviceId);
            writer.WriteInt32(SlotIndex);
            writer.WriteInt32(LogicSlotTypeInt);
            writer.WriteDouble(Value);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            DeviceId = reader.ReadInt64();
            SlotIndex = reader.ReadInt32();
            LogicSlotTypeInt = reader.ReadInt32();
            Value = reader.ReadDouble();
        }

        public void Process(long clientId)
        {
            if (!GameManager.RunSimulation) return;

            if (!(Thing.Find(DeviceId) is Device device))
                return;

            if (device.Slots == null || SlotIndex < 0 || SlotIndex >= device.Slots.Count)
                return;

            var logicSlotType = (LogicSlotType)LogicSlotTypeInt;

            if (!device.CanLogicWrite(logicSlotType, SlotIndex))
                return;

            device.SetLogicValue(logicSlotType, SlotIndex, Value);
        }
    }
}
