// #196.2: a Fluent text field copies aria-describedby onto the input inside its shadow root, where the id of the
// message holder (in the page) cannot be found, so a screen reader got no description for the field. Linking the
// input to the holder as an element (ARIA element reflection) crosses the shadow boundary, and the description then
// follows the holder's text as messages come and go. A field whose host is itself the control (fluent-select, the
// combobox) has no inner input; its own aria-describedby already resolves, so it is left alone.
export async function link(holderId) {
  const holder = document.getElementById(holderId);
  const host = holder && document.querySelector(`[aria-describedby="${CSS.escape(holderId)}"]`);
  if (!host) return;
  const control = await innerControl(host);
  if (control && "ariaDescribedByElements" in control) control.ariaDescribedByElements = [holder];
}

// The input or textarea inside the field's shadow root, once the component has drawn it (a few frames at most).
async function innerControl(host) {
  for (let frame = 0; frame < 30; frame++) {
    const control = host.shadowRoot?.querySelector("input, textarea");
    if (control) return control;
    await new Promise((resolve) => requestAnimationFrame(resolve));
  }
  return null;
}
