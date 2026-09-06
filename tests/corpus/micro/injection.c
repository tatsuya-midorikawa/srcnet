/* Names and comments below contain characters that are dangerous in HTML and
   JavaScript contexts. They must never be able to escape the embedded data. */

// TODO: </script><script>globalThis.__srcnet_injected = true;</script> and "quotes" & <tags>
int injection_target(void) { return 0; }
