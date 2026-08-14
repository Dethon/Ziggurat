//! What a keyboard hook should do with one key event.
//!
//! The Win32 plumbing around this is verified by hand, but the decision itself is a table with no
//! platform in it, and it is the part that can be wrong in ways nobody notices: a swallowed key
//! that should have reached the window, an auto-repeat read as a second press, a learn-mode key
//! typed into the editor underneath. So it lives here, and is tested in WSL.

use crate::host::{HostEvent, KeyCode};

/// What the hook does with the key it was handed.
#[derive(Clone, Debug, PartialEq)]
pub enum Decision {
    /// Let it through to whatever is in front.
    Pass,
    /// Keep it: it never reaches the window in front, and never toggles its own state.
    Swallow(Option<HostEvent>),
}

/// The hook's whole memory. A callback has to answer synchronously, so this cannot be asked of
/// the core; the core pushes the bound keys in instead.
#[derive(Default)]
pub struct KeySwitch {
    bound: Vec<KeyCode>,
    /// The binding currently held, so a key's own auto-repeat is not a second dictation.
    held: Option<KeyCode>,
    /// A key whose release is still owed a swallow but means nothing: the key just learned, and
    /// any second binding pressed while a dictation is live. Keeping it apart from `held` is what
    /// stops either one being reported as a dictation ending.
    silenced: Option<KeyCode>,
    /// Armed by the tray's "set binding" menu: the next key pressed becomes that binding's.
    learning: Option<usize>,
}

impl KeySwitch {
    pub fn set_bindings(&mut self, keys: &[KeyCode]) {
        self.bound = keys.to_vec();
    }

    pub fn learn(&mut self, binding: usize) {
        self.learning = Some(binding);
    }

    pub fn on_key(&mut self, key: KeyCode, down: bool, up: bool) -> Decision {
        // Learn mode reads exactly one key-down, and swallows that key's key-up too, so the key
        // being learned never reaches whatever is in front either.
        if let Some(binding) = self.learning {
            if down {
                self.learning = None;
                self.silenced = Some(key);
                return Decision::Swallow(Some(HostEvent::BindingLearned { binding, key }));
            }
            return Decision::Swallow(None);
        }

        if self.silenced == Some(key) {
            if up {
                self.silenced = None;
            }
            return Decision::Swallow(None);
        }

        if self.held == Some(key) {
            if up {
                self.held = None;
                return Decision::Swallow(Some(HostEvent::BindingUp(key)));
            }
            return Decision::Swallow(None);
        }

        if down && self.bound.contains(&key) {
            if self.held.is_some() {
                // A second binding while one is live. The core would ignore it anyway, so there
                // is nothing to say — but it is still a binding key and must not land in the
                // window as a literal keystroke.
                self.silenced = Some(key);
                return Decision::Swallow(None);
            }
            self.held = Some(key);
            return Decision::Swallow(Some(HostEvent::BindingDown(key)));
        }
        Decision::Pass
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const F13: KeyCode = KeyCode(0x7C);
    const F14: KeyCode = KeyCode(0x7D);
    const A: KeyCode = KeyCode(0x41);

    fn armed(bound: &[KeyCode]) -> KeySwitch {
        let mut switch = KeySwitch::default();
        switch.set_bindings(bound);
        switch
    }

    #[test]
    fn an_unbound_key_is_left_entirely_alone() {
        let mut switch = armed(&[F13]);

        assert_eq!(switch.on_key(A, true, false), Decision::Pass);
        assert_eq!(switch.on_key(A, false, true), Decision::Pass);
    }

    #[test]
    fn a_bound_key_is_swallowed_going_down_and_coming_up() {
        // Holding it must not also type an F13 into the editor or fire that application's own
        // shortcut, and the key-up has to be swallowed too or the window sees a stray release.
        let mut switch = armed(&[F13]);

        assert_eq!(
            switch.on_key(F13, true, false),
            Decision::Swallow(Some(HostEvent::BindingDown(F13)))
        );
        assert_eq!(
            switch.on_key(F13, false, true),
            Decision::Swallow(Some(HostEvent::BindingUp(F13)))
        );
    }

    #[test]
    fn the_keys_own_auto_repeat_does_not_start_a_second_dictation() {
        // Holding a key produces a stream of key-downs, and each one reaching the core would read
        // as a fresh press.
        let mut switch = armed(&[F13]);
        switch.on_key(F13, true, false);

        for _ in 0..5 {
            assert_eq!(switch.on_key(F13, true, false), Decision::Swallow(None));
        }
    }

    #[test]
    fn typing_while_a_binding_is_held_still_reaches_the_window() {
        // Only the binding key is swallowed. Everything else the person does mid-dictation is
        // theirs, and taking it would make the speech typist a keyboard blocker.
        let mut switch = armed(&[F13]);
        switch.on_key(F13, true, false);

        assert_eq!(switch.on_key(A, true, false), Decision::Pass);
    }

    #[test]
    fn a_second_binding_pressed_while_one_is_held_is_swallowed_and_says_nothing() {
        // It is a binding key, so it must not land in the window as a literal F14 — that is the
        // harm swallowing exists to prevent. And the core ignores a second binding during a live
        // dictation anyway, so there is nothing to tell it.
        let mut switch = armed(&[F13, F14]);
        switch.on_key(F13, true, false);

        assert_eq!(switch.on_key(F14, true, false), Decision::Swallow(None));
        assert_eq!(switch.on_key(F14, false, true), Decision::Swallow(None));
    }

    #[test]
    fn the_key_just_learned_is_not_reported_as_a_dictation_ending() {
        // Its key-up has to be swallowed — it must not reach the window either — but swallowing
        // it is not the same as claiming a dictation just ended on it.
        let mut switch = armed(&[F13]);
        switch.learn(1);
        switch.on_key(A, true, false);

        assert_eq!(switch.on_key(A, false, true), Decision::Swallow(None));
    }

    #[test]
    fn learn_mode_takes_one_key_and_that_key_never_reaches_the_window() {
        let mut switch = armed(&[F13]);
        switch.learn(1);

        let pressed = switch.on_key(A, true, false);
        let released = switch.on_key(A, false, true);

        assert_eq!(
            pressed,
            Decision::Swallow(Some(HostEvent::BindingLearned { binding: 1, key: A }))
        );
        assert_eq!(released, Decision::Swallow(None));
        assert_eq!(switch.on_key(A, true, false), Decision::Pass, "learn mode reads one key");
    }

    #[test]
    fn a_rebound_key_takes_over_from_the_old_one() {
        let mut switch = armed(&[F13]);
        switch.set_bindings(&[A]);

        assert_eq!(switch.on_key(F13, true, false), Decision::Pass);
        assert_eq!(
            switch.on_key(A, true, false),
            Decision::Swallow(Some(HostEvent::BindingDown(A)))
        );
    }
}
