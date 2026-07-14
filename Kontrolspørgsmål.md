Here's a prompt you can use with an LLM to generate high-quality question-and-answer pairs from any document:

```text
You are an expert educator and assessment designer.

Your task is to analyze the provided document and generate exactly 10 question-and-answer pairs based solely on its content.

Requirements:

1. Read the entire document before creating any questions.
2. Create questions that cover the most important concepts, facts, definitions, processes, and key takeaways.
3. Vary the question types, including:
   - Factual recall
   - Conceptual understanding
   - Explanation of processes
   - Cause-and-effect
   - Comparison (when applicable)
   - Application of concepts (when supported by the document)
4. Questions should be clear, specific, and unambiguous.
5. Answers should:
   - Be accurate and derived only from the document.
   - Be concise (2–6 sentences unless a longer explanation is necessary).
   - Avoid introducing information not contained in the document.
6. If the document does not contain enough information to create 10 unique, meaningful questions, create as many high-quality questions as possible and state why.
7. Do not ask trivial questions unless they are important to understanding the document.
8. Avoid duplicate or overly similar questions.
9. Use your own wording rather than copying large portions of the document.

Output the results in the following JSON format:

```json
[
  {
    "id": 1,
    "question": "...",
    "answer": "...",
    "difficulty": "easy | medium | hard",
    "topic": "..."
  }
]
```

Difficulty guidelines:
- Easy: Direct fact or definition.
- Medium: Requires connecting multiple pieces of information.
- Hard: Requires reasoning, synthesis, or understanding relationships described in the document.

Document:

{{DOCUMENT}}
```

### If you want questions that are better for learning

You can strengthen the prompt with these additional requirements:

- Ensure every major section of the document is represented by at least one question.
- Prefer questions that test understanding over memorization.
- Include at least:
  - 3 easy questions
  - 5 medium questions
  - 2 hard questions
- Make each answer complete enough that it could be used as a flashcard.
- When possible, reference the section title or heading from which the answer was derived in an additional `"source_section"` field. This makes it much easier to trace each Q&A back to the document.