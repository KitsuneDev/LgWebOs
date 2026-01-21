using Guss.Communications.ModuleFramework.Events.EventArguments;

namespace LgWebOs.Events
{
    public class UShortEventArgs : GenericEventArgs<ushort>
    {
        public UShortEventArgs()
        {
        }

        public UShortEventArgs(ushort payload) : base(payload)
        {
            
        }
    }
}