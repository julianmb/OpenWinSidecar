name: Feature request
description: Suggest something the project should do
labels: ["enhancement"]
body:
  - type: textarea
    id: problem
    attributes:
      label: What are you trying to do?
      description: Describe the situation, not the solution. I often solve these differently than you expect.
    validations:
      required: true

  - type: textarea
    id: proposal
    attributes:
      label: What you have in mind
    validations:
      required: false

  - type: dropdown
    id: scope
    attributes:
      label: Rough scope
      description: Honest sizing helps; it changes whether I take it.
      options:
        - "Small — a few hours"
        - "Medium — a day or two"
        - "Large — a rewrite or new subsystem"
        - "I genuinely don't know"
    validations:
      required: true

  - type: checkboxes
    id: contrib
    attributes:
      label: Willingness
      options:
        - label: I would be willing to work on this
          required: false
        - label: I can test on hardware you don't have
          required: false
