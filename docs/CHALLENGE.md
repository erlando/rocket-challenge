# 🪐 Backend Engineer Challenge: Rockets 🚀

## Introduction 👋
Thank you for taking Lunar's backend engineer code challenge!

In the ZIP-file you have received, you will find a `README.md` (dah! of course) and folders 
containing executables for various operating systems and architectures.

> **Important:** If you cannot find an executable that works for your system, please reach out to us as soon as possible
> so we can get you one that works.

We hope you enjoy the challenge. Good luck.

## Assessment 📝
We assess your submission based on criteria such as:

- **Design choices and trade-offs**: how clearly you explain the choices you made, how well the design supports future requirements such as scale, and which limitations you consciously accepted in favor of a simpler solution.
- **Depth and maintainability of the implementation**: your choices and reasoning around data structures, concurrency, persistence, and error handling; how understandable the code is; and how maintainable the solution is. We may drill into specific parts of your code.
- **Verification**: what you did to show the code works, e.g. automated tests.

### AI 🤖
We **encourage** you to use AI to its full extent while solving this challenge. Coding assistants, agents, and research are all fair game. Treat this as a realistic example of how you would work day-to-day.

**Your use of AI is part of how we assess the challenge.** We are not scoring "how much AI you used" as a quantity. We
are looking for a **deliberate, accountable workflow**: what you delegated, what you verified yourself, and how you kept
ownership of correctness and design. This sits alongside the solution itself. We expect you to understand the problem
space deeply and to stand behind the code you submit.

Please be prepared to cover the following in the interview.

- **Describe how you used AI at a high level**: what kinds of tasks you delegated, what you verified yourself, and where
  you overrode or rewrote AI output.
- **Reflect on agentic coding**: what worked well, what was risky, how you kept quality high through reviews, tests, and incremental steps, and what you would do differently in a larger or production system.

A strong submission is one where the implementation holds up under technical scrutiny and you can clearly articulate your
understanding, your verification habits, and a thoughtful approach to working with AI.

## The Challenge 🧑‍💻
In this challenge, you will build one or more services that consume messages from a set of
_rockets_ and expose their state through a REST API. We imagine this API being used by
something like a dashboard, but you are not expected to build the dashboard itself.

At a minimum, we expect endpoints that can:
1. Return the current state of a given rocket (type, speed, mission, etc.)
1. Return a list of all the rockets in the system; preferably with some kind of sorting.

The service should also expose an endpoint that the test program can post messages to. See this
[section](#running-the-test-program).

We write our services in [Go](https://go.dev/), but there are no constraints on which language
you choose for the challenge.
We would rather see a great solution in a language you are comfortable with than a mediocre
solution written in Go.

### The messages ✉️
Each rocket will be dispatching various messages (encoded as JSON) about its state changes through individual radio _channels_.
The channel is unique for each rocket and can therefore be treated as the ID of the rocket.

In addition to the channel, each message contains a _message number_, which represents the
message's order within a channel; a _message time_, which indicates when the message was sent;
and a _message type_, which describes the event that occurred.

**Important:** Messages will arrive **out of order**, and delivery follows an **at-least-once guarantee**, which means you may receive the same message more than once.
Messages are **redelivered** if not successfully delivered, e.g. when receiving a non-2xx HTTP status code.

Here is an example of a `RocketLaunched` message:

```json
{
    "metadata": {
        "channel": "193270a9-c9cf-404a-8f83-838e71d9ae67",
        "messageNumber": 1,    
        "messageTime": "2022-02-02T19:39:05.86337+01:00",                                          
        "messageType": "RocketLaunched"                             
    },
    "message": {                                                    
        "type": "Falcon-9",
        "launchSpeed": 500,
        "mission": "ARTEMIS"  
    }
}
```

The possible message types are:

#### `RocketLaunched`
Sent out once: when a rocket is launched for the first time.
```json
{
    "type": "Falcon-9",
    "launchSpeed": 500,
    "mission": "ARTEMIS"  
}
```

#### `RocketSpeedIncreased`
Continuously sent out: when the speed of a rocket is increased by a certain amount.
```json
{
    "by": 3000
}
```

#### `RocketSpeedDecreased`
Continuously sent out: when the speed of a rocket is decreased by a certain amount.
```json
{
    "by": 2500
}
```

#### `RocketExploded`
Sent out once: if a rocket explodes due to an accident/malfunction.
```json
{
    "reason": "PRESSURE_VESSEL_FAILURE"
}
```

#### `RocketMissionChanged`
Continuously sent out: when the mission for a rocket is changed.
```json
{
    "newMission":"SHUTTLE_MIR"
}
```

### Running the test program 💽
In the ZIP file, locate the executable that matches your system and run:

```bash
./rockets launch "http://localhost:8088/messages" --message-delay=500ms --concurrency-level=1
```

This launches the program, which starts posting (request method: `POST`) messages to the URL provided with a delay of 500ms between each message.

To see all commands run `./rockets help` and for help on the `launch` command run `./rockets launch --help`.

> We are going to run the program against your solution with the default values.

### Submitting your solution 📤
We do not expect you to spend more than **6 hours** on this challenge.  
If you do not succeed in completing everything, then submit what you have, so we have something to look at - that is much better than nothing! ☺️

Before submitting your solution please make sure that you have included all the necessary files and information for 
running and assessing your solution. You can either submit a ZIP-file or provide a link to an online version control provider like GitHub, GitLab, or Bitbucket.

### On the day of the interview 📅
You should bring your own computer and prepare to present your solution.  
We are looking for how you went about solving the problem, which challenges you met along the way, how you went about solving them and the design choices you made.  
Go breadth-first, and aim for 10 minutes. We will ask questions about the details.
