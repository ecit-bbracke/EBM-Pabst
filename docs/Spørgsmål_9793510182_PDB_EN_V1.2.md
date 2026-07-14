[
  {
    "id": 1,
    "question": "What type of product is the RLF100-11/18/2HP, and what are its main mechanical characteristics?",
    "answer": "The RLF100-11/18/2HP is a blower with clockwise rotor rotation and radial air outlet. It measures 127 mm × 127 mm × 25.4 mm, weighs 0.300 kg, uses a ball bearing system, and has a mixed-material housing with a plastic impeller. The blower can be mounted with the shaft in any orientation.",
    "difficulty": "easy",
    "topic": "General Specifications",
    "source_section": "1 General; 2.1 General"
  },
  {
    "id": 2,
    "question": "How are the electrical connections arranged, and what is the function of each wire?",
    "answer": "The blower uses four AWG 26 wires with a lead length of 310 mm. The red wire supplies positive voltage (+UB), the blue wire is ground (-GND), the violet wire is the PWM control input, and the white wire provides the tacho output. All wires have an insulation diameter of 1.35 mm.",
    "difficulty": "easy",
    "topic": "Electrical Connections",
    "source_section": "2.2 Connections"
  },
  {
    "id": 3,
    "question": "How is the blower speed controlled, and what PWM frequency range is supported?",
    "answer": "The blower is controlled through a PWM input. The supported PWM frequency range is 2 kHz to 5 kHz. According to the characteristic curve, increasing the PWM duty cycle increases the rotational speed until the maximum operating speed is reached.",
    "difficulty": "medium",
    "topic": "PWM Speed Control",
    "source_section": "3.1 Electrical Interface - Input"
  },
  {
    "id": 4,
    "question": "What are the nominal electrical operating characteristics of the blower at 48 V?",
    "answer": "At the nominal voltage of 48 V, the blower consumes approximately 17 W of power and draws about 355 mA under free-air conditions. Its nominal rotational speed is 6,400 rpm, and the specified starting current does not exceed 1.8 A.",
    "difficulty": "medium",
    "topic": "Electrical Operating Data",
    "source_section": "3.2 Electrical Operating Data"
  },
  {
    "id": 5,
    "question": "How does the tacho output indicate blower speed, and what external component is required?",
    "answer": "The blower provides an open-collector tacho output that generates two pulses per revolution, producing a frequency proportional to rotational speed using the formula (2 × n) / 60. Because the output is open collector, an external pull-up resistor connected between the supply voltage and the tacho output is required.",
    "difficulty": "medium",
    "topic": "Tacho Output",
    "source_section": "3.3 Electrical Interface - Output"
  },
  {
    "id": 6,
    "question": "What protective electrical features are built into the blower, and how do they improve reliability?",
    "answer": "The blower includes reverse-polarity protection using a rectifying diode and automatic locked-rotor restart protection. During a locked-rotor condition, it periodically attempts to restart the motor with a typical timing sequence of 0.4 seconds on and 20 seconds off, helping protect the electronics while allowing automatic recovery from temporary faults.",
    "difficulty": "hard",
    "topic": "Protection Features",
    "source_section": "3.4 Electrical Features"
  },
  {
    "id": 7,
    "question": "What aerodynamic performance is specified for the blower under standard test conditions?",
    "answer": "Under standardized laboratory conditions, the blower operates at 6,400 rpm with a 100% PWM duty cycle. It delivers a maximum free-air flow of 80.0 m³/h and a maximum static pressure of 500 Pa. The document notes that installed performance may differ from laboratory measurements depending on the application.",
    "difficulty": "medium",
    "topic": "Aerodynamic Performance",
    "source_section": "3.5 Aerodynamics"
  },
  {
    "id": 8,
    "question": "What environmental conditions is the blower designed for, and what limitations apply to its use?",
    "answer": "The blower is intended for sheltered indoor environments with controlled temperature and humidity. It operates between -20 °C and 70 °C and can be stored between -40 °C and 80 °C. Direct exposure to water, dust, and salt fog is not permitted, and the product is designed for Pollution Degree 1 environments.",
    "difficulty": "medium",
    "topic": "Environmental Requirements",
    "source_section": "4 Environment"
  },
  {
    "id": 9,
    "question": "Which electromagnetic compatibility (EMC) and electrical safety standards does the blower comply with?",
    "answer": "The blower meets EMC requirements for radiated emissions to EN 55032 Class B and electrostatic discharge immunity according to EN 61000-4-2. It is classified as Protection Class III and carries CE, EAC, UL, VDE, CSA, and CCC approvals, demonstrating compliance with multiple international safety standards.",
    "difficulty": "hard",
    "topic": "EMC and Safety",
    "source_section": "4.3 EMC; 5 Safety"
  },
  {
    "id": 10,
    "question": "How does operating temperature influence the expected lifetime of the blower?",
    "answer": "The specified L10 life expectancy is 72,500 hours at an ambient temperature of 40 °C. At the maximum permitted operating temperature, the expected L10 lifetime decreases to 35,000 hours. According to IPC 9591 calculations, the L10 life at 40 °C is 122,500 hours, illustrating the impact of higher operating temperatures on long-term reliability.",
    "difficulty": "medium",
    "topic": "Reliability",
    "source_section": "6 Reliability"
  }
]