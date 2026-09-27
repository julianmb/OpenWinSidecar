name: Bug report
description: Something does not work, or works worse than it should
labels: ["bug"]
body:
  - type: markdown
    attributes:
      value: |
        Thanks for filing this. The single most useful thing you can attach is the viewer's
        telemetry — the `[Stats]` line in the browser console, posted every five seconds.
        The **Log** panel's *Save log…* button writes `%LOCALAPPDATA%\OpenWinSidecar\logs\sidecar.log`.

  - type: textarea
    id: what-happened
    attributes:
      label: What happened
      description: What you saw, and what you expected instead.
    validations:
      required: true

  - type: textarea
    id: stats
    attributes:
      label: Viewer `[Stats]` telemetry
      description: Paste the JSON line here if you can. It usually identifies the problem immediately.
      render: text
    validations:
      required: false

  - type: textarea
    id: steps
    attributes:
      label: Steps to reproduce
      placeholder: |
        1. Turn the display on
        2. Open the URL in Safari
        3. …
    validations:
      required: true

  - type: input
    id: host
    attributes:
      label: Host
      description: Windows version, and GPU make/model.
      placeholder: Windows 11 23H2, Intel Arc A380
    validations:
      required: true

  - type: input
    id: client
    attributes:
      label: Client device
      description: iPad model and iPadOS version, or browser and version.
      placeholder: iPad Air (5th gen), iPadOS 18.1
    validations:
      required: true

  - type: dropdown
    id: elevation
    attributes:
      label: Was the app running as administrator?
      options:
        - "Yes"
        - "No"
        - "Not sure"
    validations:
      required: true

  - type: checkboxes
    id: checks
    attributes:
      label: Checks
      options:
        - label: I set an access password (or the server is on a trusted network)
          required: false
        - label: This is not a security report — for those see SECURITY.md
          required: false
