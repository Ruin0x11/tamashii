use super::util;
use bytes::{Buf, BufMut, BytesMut, IntoBuf};
use num_derive::FromPrimitive;
use num_traits::FromPrimitive;
use std::result::Result;
use tokio::codec::{Decoder, Encoder};
use log::*;

use super::structs::FileEntry;

#[derive(Clone, Debug)]
pub enum CLoginAlgorithm {
    Sha1,
    Sha256,
    Md5,
}

impl From<CLoginAlgorithm> for String {
    fn from(it: CLoginAlgorithm) -> String {
        match it {
            CLoginAlgorithm::Sha1 => "SHA1",
            CLoginAlgorithm::Sha256 => "SHA256",
            CLoginAlgorithm::Md5 => "MD5",
        }
        .into()
    }
}

impl From<String> for CLoginAlgorithm {
    fn from(it: String) -> CLoginAlgorithm {
        match it.as_ref() {
            "SHA1" => CLoginAlgorithm::Sha1,
            "SHA256" => CLoginAlgorithm::Sha256,
            "MD5" => CLoginAlgorithm::Md5,
            _ => unreachable!(),
        }
    }
}

#[derive(Clone, Debug, FromPrimitive)]
pub enum CSearchKind {
    Global = 0,
    Buddies = 1,
    Room = 2,
}

#[derive(Clone, Debug)]
pub enum DaemonMsg {
    CPing {
        id: u32,
    },
    SPing {
        id: u32,
    },

    SChallenge {
        version: u32,
        challenge: String,
    },

    CLogin {
        algorithm: CLoginAlgorithm,
        challenge_response: String,
        mask: u32,
    },
    SLogin {
        success: bool,
        message: String,
        challenge: String,
    },

    SServerState {
        connected: bool,
        username: String
    },

    CSearch {
        kind: CSearchKind,
        query: String,
    },
    SSearch {
        query: String,
        token: u32,
    },

    SSearchReply {
        token: u32,
        username: String,
        slots_free: bool,
        average_speed: u32,
        queue_length: u32,
        results: Vec<FileEntry>,
        locked_results: Vec<FileEntry>,
    },
}

pub struct DaemonMsgCodec {
    cur_len: Option<usize>,
    cur_kind: Option<u32>,
}

impl DaemonMsgCodec {
    pub fn new() -> Self {
        DaemonMsgCodec {
            cur_len: None,
            cur_kind: None,
        }
    }
}

impl Encoder for DaemonMsgCodec {
    type Item = DaemonMsg;
    type Error = std::io::Error;

    fn encode(&mut self, msg: Self::Item, bytes: &mut BytesMut) -> Result<(), Self::Error> {
        use DaemonMsg::*;

        let mut buf = BytesMut::new();
        buf.reserve(4096);

        let mut code = 0;

        match msg {
            SPing { id } => {
                code = 0x0000;

                buf.put_u32_le(id);
            }
            SChallenge { version, challenge } => {
                code = 0x0001;

                buf.put_u32_le(version);
                util::pack_string(&challenge, &mut buf)
            }
            SLogin {
                success,
                message,
                challenge,
            } => {
                code = 0x0002;

                buf.put_u32_le(success as u32);
                util::pack_string(&message, &mut buf);
                util::pack_string(&challenge, &mut buf);
            }
            SServerState { connected, username } => {
                code = 0x0003;

                buf.put_u8(connected as u8);
                util::pack_string(&username, &mut buf);
            }
            SSearch { query, token } => {
                code = 0x0401;

                util::pack_string(&query, &mut buf);
                buf.put_u32_le(token);
            }
            SSearchReply {
                token,
                username,
                slots_free,
                average_speed,
                queue_length,
                results,
                locked_results,
            } => {
                code = 0x0402;

                buf.put_u32_le(token);
                util::pack_string(&username, &mut buf);
                buf.put_u8(slots_free as u8);
                buf.put_u32_le(average_speed);
                buf.put_u32_le(queue_length); // soulseek uses u64, but museek uses u32
                buf.put_u32_le(results.len() as u32);
                for entry in results.iter() {
                    util::pack_file_entry(entry, &mut buf);
                }
                buf.put_u32_le(locked_results.len() as u32);
                for entry in locked_results.iter() {
                    util::pack_file_entry(entry, &mut buf);
                }
            }
            _ => unreachable!(),
        }

        bytes.put_u32_le(buf.len() as u32 + 4);
        bytes.put_u32_le(code);
        bytes.extend(buf);
        trace!("send {:?}", bytes);

        Ok(())
    }
}

impl Decoder for DaemonMsgCodec {
    type Item = DaemonMsg;
    type Error = std::io::Error;

    fn decode(&mut self, buf: &mut BytesMut) -> Result<Option<Self::Item>, Self::Error> {
        use DaemonMsg::*;

        if self.cur_len.is_none() {
            if buf.len() >= 4 {
                self.cur_len = Some(buf.split_to(4).into_buf().get_u32_le() as usize);
            } else {
                return Ok(None);
            }
        }
        if self.cur_kind.is_none() {
            if buf.len() >= 4 {
                self.cur_kind = Some(buf.split_to(4).into_buf().get_u32_le());
            } else {
                return Ok(None);
            }
        }

        // len contains u32 kind, but it was read already, so skip over it
        let len = self.cur_len.unwrap() - 4;
        let kind = self.cur_kind.unwrap();

        if buf.len() < len {
            return Ok(None);
        }

        // message is valid, do parse

        self.cur_len = None;
        self.cur_kind = None;

        let mut b = buf.split_to(len).into_buf();

        let result = match kind {
            0x0000 => {
                let id = b.get_u32_le();

                Ok(Some(CPing { id: id }))
            }
            // 0x0001
            0x0002 => {
                let algorithm = util::get_string2(&mut b).into();
                let challenge_response = util::get_string2(&mut b);
                let mask = b.get_u32_le();

                Ok(Some(CLogin {
                    algorithm: algorithm,
                    challenge_response: challenge_response,
                    mask: mask,
                }))
            }
            0x0401 => {
                let kind = FromPrimitive::from_u32(b.get_u32_le()).unwrap();
                let query = util::get_string2(&mut b);

                Ok(Some(CSearch {
                    kind: kind,
                    query: query,
                }))
            }
            _ => {
                warn!("Unknown daemon message {}", kind);
                Ok(None)
            }
        };

        debug!("DAEMON: get msg: {:?}", result);

        result
    }
}
