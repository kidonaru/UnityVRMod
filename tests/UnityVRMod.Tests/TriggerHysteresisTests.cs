using UnityVRMod.Core;
using Xunit;

public class TriggerHysteresisTests
{
    [Fact] public void Press_threshold_turns_on_above_0_7()
        => Assert.True(TriggerHysteresis.Update(wasHeld: false, axis: 0.71f, btn: false));

    [Fact] public void Below_press_threshold_stays_off()
        => Assert.False(TriggerHysteresis.Update(wasHeld: false, axis: 0.69f, btn: false));

    [Fact] public void Held_stays_on_in_deadband()
        => Assert.True(TriggerHysteresis.Update(wasHeld: true, axis: 0.5f, btn: false));

    [Fact] public void Held_releases_at_or_below_0_4()
        => Assert.False(TriggerHysteresis.Update(wasHeld: true, axis: 0.4f, btn: false));

    [Fact] public void Button_bit_forces_on_regardless_of_axis()
        => Assert.True(TriggerHysteresis.Update(wasHeld: false, axis: 0.0f, btn: true));

    [Fact] public void Held_with_button_does_not_release_even_below_threshold()
        => Assert.True(TriggerHysteresis.Update(wasHeld: true, axis: 0.1f, btn: true));
}
