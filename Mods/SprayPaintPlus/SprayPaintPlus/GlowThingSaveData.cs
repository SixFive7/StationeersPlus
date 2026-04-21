using System;
using Assets.Scripts.Objects;

namespace SprayPaintPlus
{
    // Subclass of vanilla ThingSaveData carrying the extra IsGlowing bit.
    // Inheritance (not siblingship) means vanilla's isinst ThingSaveData
    // check still passes and vanilla restores its own fields. See
    // Research/Patterns/SaveDataIsinstInheritance.md.
    //
    // The C# class name "GlowThingSaveData" is unique and does not collide
    // with vanilla's "ThingSaveData" in XmlSaveLoad.ExtraTypes, so no
    // explicit [XmlType] override is needed; the XmlSerializer uses the
    // class name by default.
    [Serializable]
    public class GlowThingSaveData : ThingSaveData
    {
        public bool IsGlowing;
    }
}

