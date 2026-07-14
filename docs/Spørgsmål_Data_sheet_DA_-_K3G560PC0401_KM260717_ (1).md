[
  {
    "id": 1,
    "question": "What type of product is the K3G560-PC04-01, and what are its main operating specifications?",
    "answer": "The K3G560-PC04-01 is an EC RadiPac single-inlet backward-curved centrifugal blower with a mounting bracket. It operates on a 400 VAC three-phase supply with a voltage range of 380–480 VAC at 50/60 Hz. Under maximum load, it has a nominal speed of 1,760 rpm, a power consumption of 5,000 W, and a current draw of 7.7 A.",
    "difficulty": "easy",
    "topic": "General Specifications",
    "source_section": "Nominal Data"
  },
  {
    "id": 2,
    "question": "What are the key mechanical and construction characteristics of the blower?",
    "answer": "The blower has a diameter of 560 mm and weighs 64.8 kg. It features an aluminum sheet rotor, a galvanized steel inlet nozzle and support plate, a black-painted steel mounting bracket, and a die-cast aluminum electronics housing. The motor uses ball bearings and has an IP55 enclosure with insulation class F.",
    "difficulty": "easy",
    "topic": "Mechanical Construction",
    "source_section": "Technical Description"
  },
  {
    "id": 3,
    "question": "What control and communication interfaces are integrated into the blower?",
    "answer": "The blower supports multiple control methods, including a 0–10 VDC or PWM control input, analog 4–20 mA sensor inputs, and RS485 communication using the MODBUS RTU protocol. It also provides integrated PID control, digital inputs, analog outputs, fault and operating status signals, and dedicated 10 VDC and 20 VDC auxiliary power outputs for external devices.",
    "difficulty": "medium",
    "topic": "Control Interfaces",
    "source_section": "Technical Description; Connection Diagram"
  },
  {
    "id": 4,
    "question": "How can the blower be enabled, disabled, or reset using Digital Input 1 (Din1)?",
    "answer": "Digital Input 1 is used to enable or disable the electronics. The blower is enabled when the input is left open or supplied with 5–50 VDC, and it is disabled when connected to GND or driven below 1 VDC. Changing the signal to below 1 VDC also triggers a software reset.",
    "difficulty": "medium",
    "topic": "Digital Inputs",
    "source_section": "Connection Diagram"
  },
  {
    "id": 5,
    "question": "How do the analog inputs support different control and monitoring applications?",
    "answer": "The blower provides two configurable analog inputs. Each input supports either a 0–10 V voltage signal or a 4–20 mA current signal, but only one mode may be used per input. This allows the blower to accept speed commands, sensor values, or process signals depending on the application configuration.",
    "difficulty": "medium",
    "topic": "Analog Interfaces",
    "source_section": "Connection Diagram"
  },
  {
    "id": 6,
    "question": "What protective and monitoring features are built into the blower, and how do they improve reliability?",
    "answer": "The blower includes reverse-polarity protection, locked-rotor protection, overtemperature protection for both the electronics and motor, undervoltage and phase-failure detection, overcurrent protection, soft start, and passive power factor correction (PFC). Together, these features protect the motor and electronics from electrical and mechanical faults while improving operational reliability.",
    "difficulty": "hard",
    "topic": "Protection Features",
    "source_section": "Technical Description"
  },
  {
    "id": 7,
    "question": "What environmental and electrical protection ratings make the blower suitable for industrial applications?",
    "answer": "The blower is designed for ambient temperatures from -25 °C to 50 °C and storage temperatures from -40 °C to 80 °C. It has an IP55 enclosure, insulation class F, environmental class H1, and complies with EMC standards for industrial immunity (EN 61000-6-2) while meeting residential emission limits under EN 61000-6-3 with specified exceptions. These characteristics make it suitable for demanding industrial environments.",
    "difficulty": "medium",
    "topic": "Environmental and Electrical Ratings",
    "source_section": "Nominal Data; Technical Description"
  },
  {
    "id": 8,
    "question": "What safety standards and certifications does the blower comply with?",
    "answer": "The blower complies with EN 61800-5-1 and carries CE and UKCA conformity. It is also approved by CSA, EAC, and UL standards, including UL 1004-7 and UL 60730-1. Protection Class I is achieved when a protective earth conductor is correctly connected during installation.",
    "difficulty": "easy",
    "topic": "Safety and Compliance",
    "source_section": "Protection Classification and Approvals"
  },
  {
    "id": 9,
    "question": "How do airflow, pressure, and power consumption relate to each other according to the performance data?",
    "answer": "The performance curves show that increasing static pressure reduces the achievable airflow, while maintaining higher pressure generally requires greater electrical power. For example, at approximately 1,000 Pa the blower delivers about 12,180 m³/h while consuming around 5,000 W. This illustrates the trade-off between airflow and pressure that is typical of centrifugal blowers.",
    "difficulty": "hard",
    "topic": "Performance Characteristics",
    "source_section": "Performance Curves (50 Hz)"
  },
  {
    "id": 10,
    "question": "What operating point corresponds to the blower's maximum efficiency according to the Eco Design data?",
    "answer": "The Eco Design data specifies the optimal operating point as approximately 11,760 m³/h at a pressure increase of 1,035 Pa. At this point, the blower operates at about 1,770 rpm with a power consumption of approximately 5.03 kW and achieves a total efficiency of 70.2%.",
    "difficulty": "medium",
    "topic": "Efficiency and Operating Point",
    "source_section": "Eco Design Data"
  }
]